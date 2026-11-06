using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;
namespace SmsPanel.Api.Services;

public sealed class InboxPollingWorker(IServiceScopeFactory scopes, IFarapayamakClient client, IOptions<FarapayamakOptions> settings, ILogger<InboxPollingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token) { while (!token.IsCancellationRequested) { if (settings.Value.PollingEnabled && !string.IsNullOrWhiteSpace(settings.Value.Username)) try { await Poll(token); } catch (Exception ex) { log.LogError(ex, "واکشی صندوق ورودی ناموفق بود."); } await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, settings.Value.PollingIntervalMinutes)), token); } }
    private async Task Poll(CancellationToken token) { for (var index = 0; ; index += settings.Value.InboxPageSize) { var list = await client.GetMessagesAsync(index, settings.Value.InboxPageSize, token); foreach (var item in list) { using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>(); if (await db.InboundMessages.AnyAsync(x => x.Provider == "Farapayamak" && x.ProviderMessageId == item.Id, token)) continue; var msg = new InboundMessage { Provider = "Farapayamak", ProviderMessageId = item.Id, SenderMobile = item.Sender, ReceiverNumber = item.Receiver, RawText = item.Text, ReceivedAtUtc = item.ReceivedAtUtc, FetchedAtUtc = DateTimeOffset.UtcNow, ProcessingStatus = ProcessingStatus.Pending }; db.Add(msg); await db.SaveChangesAsync(token); await scope.ServiceProvider.GetRequiredService<InboundMessageProcessor>().ProcessAsync(msg, token); } if (list.Count < settings.Value.InboxPageSize) break; } }
}
public sealed class InboundReprocessingWorker(IServiceScopeFactory scopes, IbanService ibans, BankCardService cards, InboundTextDecoder textDecoder, ILogger<InboundReprocessingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var encodedMessages = await db.InboundMessages.Where(x => x.RawText.Contains("\u0006") || x.RawText.Contains("\u0600") || x.RawText.Contains("&amp;#") || x.RawText.Contains("&#255;")).ToListAsync(token);
            foreach (var message in encodedMessages.Where(x => textDecoder.NeedsDecoding(x.RawText))) message.RawText = textDecoder.Decode(message.RawText);
            if (encodedMessages.Count > 0) await db.SaveChangesAsync(token);
            await db.InboundMessages
                .Where(message => message.ProcessingStatus != ProcessingStatus.Processed && db.SecondaryBankAccounts.Any(account => account.InboundMessageId == message.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(message => message.ProcessingStatus, ProcessingStatus.Processed).SetProperty(message => message.LastError, (string?)null), token);
            var candidateIds = await db.ReconciliationIssues.AsNoTracking()
                .Where(x => x.Status == IssueStatus.Open && (x.IssueType == IssueType.MobileNotFound || x.IssueType == IssueType.MobileMatchesMultiplePeople || x.IssueType == IssueType.RequestNotActive || x.IssueType == IssueType.NoIbanFound || x.IssueType == IssueType.MultipleIbansFound ||
                    (x.IssueType == IssueType.BankCardNumberReceived && db.InboundMessages.Any(message => message.Id == x.InboundMessageId && message.RawText.Contains("شبا")))))
                .Select(x => x.InboundMessageId).Distinct().ToListAsync(token);
            var candidates = await db.InboundMessages.Where(x => candidateIds.Contains(x.Id)).ToListAsync(token);
            var processor = scope.ServiceProvider.GetRequiredService<InboundMessageProcessor>();
            var reprocessed = 0;
            foreach (var message in candidates.Where(x => ibans.Extract(x.RawText).Any(ibans.IsValid) || ibans.ExtractCorrectable(x.RawText).Count > 0 || ibans.ExtractIncomplete(x.RawText).Count == 1 || cards.ExtractValid(x.RawText).Count > 0))
            {
                await db.ReconciliationIssues.Where(x => x.InboundMessageId == message.Id && x.Status == IssueStatus.Open).ExecuteDeleteAsync(token);
                message.ProcessingStatus = ProcessingStatus.Pending; message.LastError = null; await db.SaveChangesAsync(token);
                await processor.ProcessAsync(message, token, sendConfirmation: false);
                reprocessed++;
            }
            if (reprocessed > 0) log.LogInformation("{Count} پیام ورودی موجود با قواعد جدید شبا دوباره پردازش شد.", reprocessed);
        }
        catch (Exception ex) { log.LogError(ex, "بازپردازش پیام‌های ورودی موجود ناموفق بود."); }
    }
}
public sealed class OutboundDispatchWorker(IServiceScopeFactory scopes, IFarapayamakClient client, IOptions<FarapayamakOptions> settings, ILogger<OutboundDispatchWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using (var recoveryScope = scopes.CreateScope())
        {
            var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<SmsDbContext>();
            await recoveryDb.OutboundMessages.Where(x => x.Status == OutboundStatus.Processing).ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, OutboundStatus.Pending), token);
        }
        while (!token.IsCancellationRequested)
        {
            var dispatched = false;
            if (!string.IsNullOrWhiteSpace(settings.Value.Username)) try { dispatched = await Dispatch(token); } catch (Exception ex) { log.LogError(ex, "ارسال صف ناموفق بود."); }
            await Task.Delay(dispatched ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5), token);
        }
    }

    private async Task<bool> Dispatch(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Campaigns.Where(x => x.Status == CampaignStatus.Active && x.EndsAtUtc != null && x.EndsAtUtc < now)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, CampaignStatus.Closed), token);
        var rows = await db.OutboundMessages
            .Where(message => message.Status == OutboundStatus.Pending && db.Campaigns.Any(campaign => campaign.Id == message.CampaignId && campaign.Status == CampaignStatus.Active && campaign.StartsAtUtc <= now && (campaign.EndsAtUtc == null || campaign.EndsAtUtc >= now)))
            .OrderBy(x => x.Id).Take(Math.Clamp(settings.Value.SendBatchSize, 1, 100)).ToListAsync(token);
        if (rows.Count == 0) { await CloseCompletedCampaigns(db, token); return false; }
        var personIds = rows.Select(x => x.PersonId).ToList();
        var mobiles = await db.People.AsNoTracking().Where(x => personIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Mobile, token);
        foreach (var row in rows)
        {
            if (!mobiles.TryGetValue(row.PersonId, out var mobile))
            {
                row.Status = OutboundStatus.Failed;
                row.LastError = "شماره موبایل مخاطب یافت نشد.";
                continue;
            }
            row.Status = OutboundStatus.Processing; row.AttemptCount++;
        }
        await db.SaveChangesAsync(token);

        var dispatchable = rows.Where(row => row.Status == OutboundStatus.Processing).Select(row => new DispatchItem(row.Id, mobiles[row.PersonId], row.RenderedText)).ToList();
        await Parallel.ForEachAsync(dispatchable, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(settings.Value.SendConcurrency, 1, 20), CancellationToken = token }, async (item, itemToken) =>
        {
            using var itemScope = scopes.CreateScope();
            var itemDb = itemScope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var row = await itemDb.OutboundMessages.SingleAsync(x => x.Id == item.Id, itemToken);
            try
            {
                var response = await client.SendAsync([item.Mobile], item.RenderedText, itemToken);
                row.ProviderReference = response.Value;
                row.Status = response.IsSuccess ? OutboundStatus.Sent : OutboundStatus.Failed;
                row.SentAtUtc = response.IsSuccess ? DateTimeOffset.UtcNow : null;
                row.LastError = response.IsSuccess ? null : response.Value == "7"
                    ? "متن پیام توسط فیلتر سرویس رد شد."
                    : response.StrRetStatus ?? $"خطای سرویس پیامک ({response.Value})";
            }
            catch (Exception ex)
            {
                row.Status = row.AttemptCount >= 5 ? OutboundStatus.Failed : OutboundStatus.Pending;
                row.LastError = ex.Message;
            }
            await itemDb.SaveChangesAsync(itemToken);
        });
        await CloseCompletedCampaigns(db, token);
        return true;
    }

    private sealed record DispatchItem(long Id, string Mobile, string RenderedText);

    private static Task<int> CloseCompletedCampaigns(SmsDbContext db, CancellationToken token) =>
        db.Campaigns
            .Where(c => c.Status == CampaignStatus.Active && !db.OutboundMessages.Any(m => m.CampaignId == c.Id && (m.Status == OutboundStatus.Pending || m.Status == OutboundStatus.Processing)))
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, CampaignStatus.Closed), token);
}
