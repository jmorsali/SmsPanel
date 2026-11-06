using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;
namespace SmsPanel.Api.Services;

public sealed class InboundMessageProcessor(SmsDbContext db, IbanService ibans, BankCardService cards, MobileNormalizer mobiles, IFarapayamakClient client, MessageTemplateRenderer renderer, IbanOwnershipVerificationService ownership, InboundPersonNameResolver nameResolver)
{
    public async Task ProcessAsync(InboundMessage msg, CancellationToken token, bool sendConfirmation = true)
    {
        msg.SenderMobile = mobiles.Normalize(msg.SenderMobile); var people = await db.People.Where(x => x.Mobile == msg.SenderMobile).ToListAsync(token);
        var matchedByName = false;
        Person? person;
        if (people.Count != 1)
        {
            person = await nameResolver.FindUniqueAsync(msg.RawText, token); matchedByName = person is not null;
            if (person is null)
            {
                var messageIbans = ibans.Extract(msg.RawText).Concat(ibans.ExtractCorrectable(msg.RawText)).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (messageIbans.Count == 1)
                {
                    var lookup = await ownership.LookupOwnerAsync(messageIbans[0], token);
                    if (lookup.AccountHolderName is not null) { person = await nameResolver.FindUniqueByNameAsync(lookup.AccountHolderName, token); matchedByName = person is not null; }
                }
            }
            if (person is null) { await Issue(msg, people.Count == 0 ? IssueType.MobileNotFound : IssueType.MobileMatchesMultiplePeople, null, null, null, sendConfirmation, token); return; }
        }
        else person = people[0];
        if (matchedByName) { msg.MatchedPersonId = person.Id; await db.SaveChangesAsync(token); }
        var campaign = await db.OutboundMessages.Where(o => o.PersonId == person.Id && o.Status == OutboundStatus.Sent && o.SentAtUtc <= msg.ReceivedAtUtc)
            .Join(db.Campaigns.Where(c => c.Status != CampaignStatus.Draft && c.StartsAtUtc <= msg.ReceivedAtUtc && (c.EndsAtUtc == null || c.EndsAtUtc >= msg.ReceivedAtUtc)), o => o.CampaignId, c => c.Id, (o, c) => new { Campaign = c, o.SentAtUtc })
            .OrderByDescending(x => x.SentAtUtc).ThenByDescending(x => x.Campaign.Id).Select(x => x.Campaign).FirstOrDefaultAsync(token);
        if (campaign is null) { await Issue(msg, IssueType.RequestNotActive, null, person, null, sendConfirmation, token); return; }
        var found = ibans.Extract(msg.RawText).Concat(ibans.ExtractCorrectable(msg.RawText)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count == 0)
        {
            var incomplete = ibans.ExtractIncomplete(msg.RawText);
            var cardNumbers = cards.ExtractValid(msg.RawText);
            var type = incomplete.Count == 1 ? IssueType.IncompleteIban : cardNumbers.Count > 0 ? IssueType.BankCardNumberReceived : IssueType.NoIbanFound;
            await Issue(msg, type, campaign, person, incomplete.Count == 1 ? incomplete[0] : null, sendConfirmation, token);
            return;
        }
        var iban = found.FirstOrDefault(ibans.IsValid);
        if (iban is null) { await Issue(msg, IssueType.InvalidIban, campaign, person, found[0], sendConfirmation, token); return; }
        if (person.CurrentIban?.Equals(iban, StringComparison.OrdinalIgnoreCase) == true) { await Issue(msg, IssueType.IbanEqualsCurrentIban, campaign, person, iban, sendConfirmation, token); return; }
        if (await db.SecondaryBankAccounts.AnyAsync(x => x.Iban == iban && x.PersonId != person.Id, token)) { await Issue(msg, IssueType.IbanUsedByAnotherPerson, campaign, person, iban, sendConfirmation, token); return; }
        var ownershipResult = await ownership.VerifyAsync(iban, $"{person.FirstName} {person.LastName}".Trim(), token);
        if (ownershipResult.Outcome == OwnershipCheckOutcome.Mismatched)
        {
            var detail = $"نام صاحب حساب اعلام‌شده توسط بانک با نام پذیرنده مطابقت ندارد. نام بانک: {ownershipResult.AccountHolderName}؛ نام پذیرنده: {person.FirstName} {person.LastName}.";
            await Issue(msg, IssueType.AccountHolderNameMismatch, campaign, person, iban, false, token, detail); return;
        }
        var old = await db.SecondaryBankAccounts.SingleOrDefaultAsync(x => x.PersonId == person.Id && x.CampaignId == campaign.Id, token);
        if (old is not null && old.Iban != iban) { await Issue(msg, IssueType.MultipleDifferentReplies, campaign, person, iban, sendConfirmation, token); return; }
        var verified = ownershipResult.Outcome == OwnershipCheckOutcome.Matched ? true : (bool?)null;
        if (old is null) db.SecondaryBankAccounts.Add(new() { PersonId = person.Id, CampaignId = campaign.Id, InboundMessageId = msg.Id, Iban = iban, OwnershipVerified = verified, ReceivedAtUtc = msg.ReceivedAtUtc });
        else if (verified == true) old.OwnershipVerified = true;
        msg.ProcessingStatus = ProcessingStatus.Processed; await db.SaveChangesAsync(token);
        var confirmationTemplate = campaign.ConfirmationTemplate;
        if (sendConfirmation && !matchedByName && campaign.ConfirmationEnabled && !string.IsNullOrWhiteSpace(confirmationTemplate))
            await client.SendAsync([person.Mobile], renderer.RenderConfirmation(confirmationTemplate, person, iban), token);
    }
    private async Task Issue(InboundMessage msg, IssueType type, Campaign? campaign, Person? person, string? iban, bool sendAutomatedReply, CancellationToken token, string? detail = null)
    {
        db.ReconciliationIssues.Add(new() { CampaignId = campaign?.Id, InboundMessageId = msg.Id, SenderMobile = msg.SenderMobile, ExtractedIban = iban, IssueType = type });
        msg.ProcessingStatus = ProcessingStatus.ReconciliationRequired; msg.LastError = detail;
        await db.SaveChangesAsync(token);
        if (sendAutomatedReply && type != IssueType.MultipleDifferentReplies && type != IssueType.BankCardNumberReceived && campaign?.InvalidReplyEnabled == true && person is not null && !string.IsNullOrWhiteSpace(campaign.InvalidReplyTemplate))
            await client.SendAsync([person.Mobile], renderer.RenderInvalidReply(campaign.InvalidReplyTemplate, person, IssueReason(type)), token);
    }

    private static string IssueReason(IssueType type) => type switch
    {
        IssueType.NoIbanFound => "شماره شبای معتبر در پیام پیدا نشد",
        IssueType.InvalidIban => "شماره شبا نامعتبر است",
        IssueType.IncompleteIban => "شماره شبا ناقص است",
        IssueType.MultipleIbansFound => "بیش از یک شماره شبا در پیام وجود دارد",
        IssueType.IbanEqualsCurrentIban => "شماره شبا با حساب فعلی یکسان است",
        IssueType.IbanUsedByAnotherPerson => "این شماره شبا قبلاً برای شخص دیگری ثبت شده است",
        IssueType.MultipleDifferentReplies => "پاسخ جدید با پاسخ قبلی متفاوت است",
        IssueType.BankCardNumberReceived => "شماره کارت دریافت شده و تبدیل آن به شبا نیازمند بررسی اپراتور است",
        IssueType.AccountHolderNameMismatch => "نام صاحب حساب با پذیرنده مطابقت ندارد",
        _ => "پاسخ قابل پردازش نیست"
    };
}
