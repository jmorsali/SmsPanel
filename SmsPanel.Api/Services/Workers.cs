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
public sealed class OutboundDispatchWorker(IServiceScopeFactory scopes, IFarapayamakClient client, IOptions<FarapayamakOptions> settings, ILogger<OutboundDispatchWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token) { using (var recoveryScope = scopes.CreateScope()) { var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<SmsDbContext>(); await recoveryDb.OutboundMessages.Where(x => x.Status == OutboundStatus.Processing).ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, OutboundStatus.Pending), token); } while (!token.IsCancellationRequested) { if (!string.IsNullOrWhiteSpace(settings.Value.Username)) try { await Dispatch(token); } catch (Exception ex) { log.LogError(ex, "ارسال صف ناموفق بود."); } await Task.Delay(TimeSpan.FromSeconds(10), token); } }
    private async Task Dispatch(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Campaigns.Where(x => x.Status == CampaignStatus.Active && x.EndsAtUtc != null && x.EndsAtUtc < now)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, CampaignStatus.Closed), token);
        var rows = await db.OutboundMessages.Where(x => x.Status == OutboundStatus.Pending).OrderBy(x => x.Id).Take(Math.Clamp(settings.Value.SendBatchSize, 1, 100)).ToListAsync(token);
        if (rows.Count == 0) return;
        var activeCampaignIds = await db.Campaigns.Where(x => x.Status == CampaignStatus.Active && x.StartsAtUtc <= now && (x.EndsAtUtc == null || x.EndsAtUtc >= now)).Select(x => x.Id).ToListAsync(token);
        rows = rows.Where(x => activeCampaignIds.Contains(x.CampaignId)).ToList();
        var personIds = rows.Select(x => x.PersonId).ToList();
        var mobiles = await db.People.Where(x => personIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Mobile, token);
        foreach (var row in rows)
        {
            if (!mobiles.TryGetValue(row.PersonId, out var mobile)) continue;
            row.Status = OutboundStatus.Processing; row.AttemptCount++;
            await db.SaveChangesAsync(token);
            try
            {
                var response = await client.SendAsync([mobile], row.RenderedText, token);
                row.ProviderReference = response.Value;
                row.Status = response.IsSuccess ? OutboundStatus.Sent : row.AttemptCount >= 5 ? OutboundStatus.Failed : OutboundStatus.Pending;
                row.SentAtUtc = response.IsSuccess ? DateTimeOffset.UtcNow : null;
                row.LastError = response.IsSuccess ? null : response.StrRetStatus;
            }
            catch (Exception ex)
            {
                row.Status = row.AttemptCount >= 5 ? OutboundStatus.Failed : OutboundStatus.Pending;
                row.LastError = ex.Message;
            }
            await db.SaveChangesAsync(token);
        }
    }
}
