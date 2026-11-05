using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;
namespace SmsPanel.Api.Services;

public sealed class InboundMessageProcessor(SmsDbContext db, IbanService ibans, MobileNormalizer mobiles, IFarapayamakClient client, MessageTemplateRenderer renderer)
{
    public async Task ProcessAsync(InboundMessage msg, CancellationToken token, bool sendConfirmation = true)
    {
        msg.SenderMobile = mobiles.Normalize(msg.SenderMobile); var people = await db.People.Where(x => x.Mobile == msg.SenderMobile).ToListAsync(token);
        if (people.Count != 1) { await Issue(msg, people.Count == 0 ? IssueType.MobileNotFound : IssueType.MobileMatchesMultiplePeople, null, null, null, sendConfirmation, token); return; }
        var person = people[0];
        var campaign = await db.OutboundMessages.Where(o => o.PersonId == person.Id && o.Status == OutboundStatus.Sent && o.SentAtUtc <= msg.ReceivedAtUtc)
            .Join(db.Campaigns.Where(c => c.Status != CampaignStatus.Draft && c.StartsAtUtc <= msg.ReceivedAtUtc && (c.EndsAtUtc == null || c.EndsAtUtc >= msg.ReceivedAtUtc)), o => o.CampaignId, c => c.Id, (o, c) => new { Campaign = c, o.SentAtUtc })
            .OrderByDescending(x => x.SentAtUtc).ThenByDescending(x => x.Campaign.Id).Select(x => x.Campaign).FirstOrDefaultAsync(token);
        if (campaign is null) { await Issue(msg, IssueType.RequestNotActive, null, person, null, sendConfirmation, token); return; }
        var found = ibans.Extract(msg.RawText).Concat(ibans.ExtractCorrectable(msg.RawText)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count == 0) { var incomplete = ibans.ExtractIncomplete(msg.RawText); await Issue(msg, incomplete.Count == 1 ? IssueType.IncompleteIban : IssueType.NoIbanFound, campaign, person, incomplete.Count == 1 ? incomplete[0] : null, sendConfirmation, token); return; }
        var iban = found.FirstOrDefault(ibans.IsValid);
        if (iban is null) { await Issue(msg, IssueType.InvalidIban, campaign, person, found[0], sendConfirmation, token); return; }
        if (person.CurrentIban?.Equals(iban, StringComparison.OrdinalIgnoreCase) == true) { await Issue(msg, IssueType.IbanEqualsCurrentIban, campaign, person, iban, sendConfirmation, token); return; }
        if (await db.SecondaryBankAccounts.AnyAsync(x => x.Iban == iban && x.PersonId != person.Id, token)) { await Issue(msg, IssueType.IbanUsedByAnotherPerson, campaign, person, iban, sendConfirmation, token); return; }
        var old = await db.SecondaryBankAccounts.SingleOrDefaultAsync(x => x.PersonId == person.Id && x.CampaignId == campaign.Id, token);
        if (old is not null && old.Iban != iban) { await Issue(msg, IssueType.MultipleDifferentReplies, campaign, person, iban, sendConfirmation, token); return; }
        if (old is null) db.SecondaryBankAccounts.Add(new() { PersonId = person.Id, CampaignId = campaign.Id, InboundMessageId = msg.Id, Iban = iban, OwnershipVerified = false, ReceivedAtUtc = msg.ReceivedAtUtc });
        msg.ProcessingStatus = ProcessingStatus.Processed; await db.SaveChangesAsync(token);
        var confirmationTemplate = campaign.ConfirmationTemplate;
        if (sendConfirmation && campaign.ConfirmationEnabled && !string.IsNullOrWhiteSpace(confirmationTemplate))
            await client.SendAsync([person.Mobile], renderer.RenderConfirmation(confirmationTemplate, person, iban), token);
    }
    private async Task Issue(InboundMessage msg, IssueType type, Campaign? campaign, Person? person, string? iban, bool sendAutomatedReply, CancellationToken token)
    {
        db.ReconciliationIssues.Add(new() { CampaignId = campaign?.Id, InboundMessageId = msg.Id, SenderMobile = msg.SenderMobile, ExtractedIban = iban, IssueType = type });
        msg.ProcessingStatus = ProcessingStatus.ReconciliationRequired;
        await db.SaveChangesAsync(token);
        if (sendAutomatedReply && campaign?.InvalidReplyEnabled == true && person is not null && !string.IsNullOrWhiteSpace(campaign.InvalidReplyTemplate))
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
        _ => "پاسخ قابل پردازش نیست"
    };
}
