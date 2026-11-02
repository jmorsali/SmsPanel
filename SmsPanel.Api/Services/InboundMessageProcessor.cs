using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;
namespace SmsPanel.Api.Services;

public sealed class InboundMessageProcessor(SmsDbContext db, IbanService ibans, MobileNormalizer mobiles, IFarapayamakClient client, MessageTemplateRenderer renderer)
{
    public async Task ProcessAsync(InboundMessage msg, CancellationToken token)
    {
        msg.SenderMobile = mobiles.Normalize(msg.SenderMobile); var people = await db.People.Where(x => x.Mobile == msg.SenderMobile).ToListAsync(token);
        if (people.Count != 1) { await Issue(msg, people.Count == 0 ? IssueType.MobileNotFound : IssueType.MobileMatchesMultiplePeople, null, null, token); return; }
        var person = people[0]; var campaigns = await db.Campaigns.Where(x => x.Status == CampaignStatus.Active && x.StartsAtUtc <= msg.ReceivedAtUtc && (x.EndsAtUtc == null || x.EndsAtUtc >= msg.ReceivedAtUtc)).Where(x => db.OutboundMessages.Any(o => o.CampaignId == x.Id && o.PersonId == person.Id && o.Status == OutboundStatus.Sent)).ToListAsync(token);
        if (campaigns.Count != 1) { await Issue(msg, campaigns.Count == 0 ? IssueType.RequestNotActive : IssueType.RequestAmbiguous, null, null, token); return; }
        var found = ibans.Extract(msg.RawText); if (found.Count != 1) { await Issue(msg, found.Count == 0 ? IssueType.NoIbanFound : IssueType.MultipleIbansFound, campaigns[0].Id, null, token); return; }
        var iban = found[0];
        if (!ibans.IsValid(iban)) { await Issue(msg, IssueType.InvalidIban, campaigns[0].Id, iban, token); return; }
        if (person.CurrentIban?.Equals(iban, StringComparison.OrdinalIgnoreCase) == true) { await Issue(msg, IssueType.IbanEqualsCurrentIban, campaigns[0].Id, iban, token); return; }
        if (await db.SecondaryBankAccounts.AnyAsync(x => x.Iban == iban && x.PersonId != person.Id, token)) { await Issue(msg, IssueType.IbanUsedByAnotherPerson, campaigns[0].Id, iban, token); return; }
        var old = await db.SecondaryBankAccounts.SingleOrDefaultAsync(x => x.PersonId == person.Id && x.CampaignId == campaigns[0].Id, token);
        if (old is not null && old.Iban != iban) { await Issue(msg, IssueType.MultipleDifferentReplies, campaigns[0].Id, iban, token); return; }
        if (old is null) db.SecondaryBankAccounts.Add(new() { PersonId = person.Id, CampaignId = campaigns[0].Id, InboundMessageId = msg.Id, Iban = iban, OwnershipVerified = false, ReceivedAtUtc = msg.ReceivedAtUtc });
        msg.ProcessingStatus = ProcessingStatus.Processed; await db.SaveChangesAsync(token);
        var confirmationTemplate = campaigns[0].ConfirmationTemplate;
        if (campaigns[0].ConfirmationEnabled && !string.IsNullOrWhiteSpace(confirmationTemplate))
            await client.SendAsync([person.Mobile], renderer.RenderConfirmation(confirmationTemplate, person, iban), token);
    }
    private async Task Issue(InboundMessage msg, IssueType type, long? campaign, string? iban, CancellationToken token) { db.ReconciliationIssues.Add(new() { CampaignId = campaign, InboundMessageId = msg.Id, SenderMobile = msg.SenderMobile, ExtractedIban = iban, IssueType = type }); msg.ProcessingStatus = ProcessingStatus.ReconciliationRequired; await db.SaveChangesAsync(token); }
}
