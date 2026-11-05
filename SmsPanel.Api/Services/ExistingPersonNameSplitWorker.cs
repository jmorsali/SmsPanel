using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;

namespace SmsPanel.Api.Services;

public sealed class ExistingPersonNameSplitWorker(
    IServiceScopeFactory scopes,
    PersianPersonNameParser parser,
    ILogger<ExistingPersonNameSplitWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
            var people = await db.People
                .Where(x => (x.LastName == null || x.LastName == "") && x.FirstName.Contains(" "))
                .ToListAsync(token);

            var changed = 0;
            foreach (var person in people)
            {
                var name = parser.Parse(person.FirstName);
                if (name.LastName.Length == 0) continue;

                person.FirstName = name.FirstName;
                person.LastName = name.LastName;
                changed++;
            }

            if (changed > 0) await db.SaveChangesAsync(token);
            log.LogInformation("Split full names for {Count} existing people.", changed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not split existing person names.");
        }
    }
}
