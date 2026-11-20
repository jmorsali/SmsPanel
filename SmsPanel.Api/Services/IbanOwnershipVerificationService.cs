using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;

namespace SmsPanel.Api.Services;

public enum OwnershipCheckOutcome { Matched, Mismatched, Inconclusive }
public sealed record OwnershipCheckResult(OwnershipCheckOutcome Outcome, string? AccountHolderName, string? Error = null);

public interface IIbanOwnershipVerificationService
{
    Task<(string? AccountHolderName, string? Error)> LookupOwnerAsync(string iban, CancellationToken token);
    Task<OwnershipCheckResult> VerifyAsync(string iban, string expectedName, CancellationToken token);
}

public sealed class IbanOwnershipVerificationService(BsiCardToIbanService bsi) : IIbanOwnershipVerificationService
{
    public async Task<(string? AccountHolderName, string? Error)> LookupOwnerAsync(string iban, CancellationToken token)
    {
        var normalized = "IR" + new string(iban.Skip(2).Where(char.IsDigit).ToArray());
        if (normalized.Length != 26 || normalized.Substring(4, 3) != "019") return (null, "برای این بانک هنوز استعلام مالکیت فعال نشده است.");
        try
        {
            var owner = await bsi.LookupIbanOwnerAsync(normalized, token);
            return string.IsNullOrWhiteSpace(owner) ? (null, "بانک نام صاحب حساب را برنگرداند.") : (owner, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) { return (null, ex.Message); }
    }

    public async Task<OwnershipCheckResult> VerifyAsync(string iban, string expectedName, CancellationToken token)
    {
        var lookup = await LookupOwnerAsync(iban, token);
        if (lookup.AccountHolderName is null) return new(OwnershipCheckOutcome.Inconclusive, null, lookup.Error);
        return PersonNameMatcher.Matches(expectedName, lookup.AccountHolderName) ? new(OwnershipCheckOutcome.Matched, lookup.AccountHolderName) : new(OwnershipCheckOutcome.Mismatched, lookup.AccountHolderName);
    }
}

public static class PersonNameMatcher
{
    public static bool Matches(string expected, string actual)
    {
        var expectedParts = Parts(expected); var actualParts = Parts(actual);
        if (expectedParts.Length == 0 || actualParts.Length == 0) return false;
        var expectedSet = expectedParts.ToHashSet(StringComparer.Ordinal); var actualSet = actualParts.ToHashSet(StringComparer.Ordinal);
        if (expectedSet.SetEquals(actualSet)) return true;
        return SimilarityScore(expectedParts, actualParts) >= 0.90;
    }

    public static double SimilarityScore(string expected, string actual) => SimilarityScore(Parts(expected), Parts(actual));
    public static string[] Parts(string value) => Regex.Split(value.Normalize(NormalizationForm.FormKC)
        .Replace('ي', 'ی').Replace('ى', 'ی').Replace('ئ', 'ی').Replace('ك', 'ک').Replace('ة', 'ه').Replace('ۀ', 'ه')
        .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ؤ', 'و').Replace("‌", " "), @"\s+")
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray())).Where(x => x.Length > 1).ToArray();

    private static double SimilarityScore(string[] expectedParts, string[] actualParts)
    {
        if (expectedParts.Length == 0 || actualParts.Length == 0) return 0;
        var inOriginalOrder = Similarity(string.Concat(expectedParts), string.Concat(actualParts));
        var inSortedOrder = Similarity(string.Concat(expectedParts.OrderBy(x => x, StringComparer.Ordinal)), string.Concat(actualParts.OrderBy(x => x, StringComparer.Ordinal)));
        return Math.Max(inOriginalOrder, inSortedOrder);
    }

    private static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1d - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }
}

public sealed class ExistingIbanOwnershipVerificationWorker(IServiceScopeFactory scopes, ILogger<ExistingIbanOwnershipVerificationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        var rows = await db.SecondaryBankAccounts.AsNoTracking()
            .Where(x => x.OwnershipVerified != true && x.Iban.Length == 26 && x.Iban.Substring(4, 3) == "019")
            .Join(db.People.AsNoTracking(), account => account.PersonId, person => person.Id, (account, person) => new { Account = account, Person = person })
            .OrderBy(x => x.Account.OwnershipVerified == false ? 0 : 1).ThenBy(x => x.Account.Id).ToListAsync(token);
        var verifier = scope.ServiceProvider.GetRequiredService<IbanOwnershipVerificationService>();
        foreach (var row in rows)
        {
            var expectedName = $"{row.Person.FirstName} {row.Person.LastName}".Trim();
            var result = await verifier.VerifyAsync(row.Account.Iban, expectedName, token);
            if (result.Outcome == OwnershipCheckOutcome.Inconclusive)
            {
                log.LogWarning("استعلام مالکیت یکی از شباهای بانک صادرات قطعی نبود و بدون تغییر باقی ماند: {Reason}", result.Error);
                continue;
            }
            await SaveAuditResultAsync(db, row.Account, result, expectedName, token);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    private static Task SaveAuditResultAsync(SmsDbContext db, SecondaryBankAccount account, OwnershipCheckResult result,
        string expectedName, CancellationToken token)
    {
        var verified = result.Outcome == OwnershipCheckOutcome.Matched;
        return db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            operation: async cancellationToken =>
            {
                db.ChangeTracker.Clear();
                var changed = await db.SecondaryBankAccounts
                    .Where(x => x.Id == account.Id && x.InboundMessageId == account.InboundMessageId
                        && x.Iban == account.Iban && x.OwnershipVerified != true)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(x => x.OwnershipVerified, verified), cancellationToken);
                if (changed == 0) return;

                var message = await db.InboundMessages.SingleAsync(x => x.Id == account.InboundMessageId, cancellationToken);
                var ownershipIssues = await db.ReconciliationIssues.Where(x => x.InboundMessageId == message.Id
                    && x.Status == IssueStatus.Open && x.IssueType == IssueType.AccountHolderNameMismatch).ToListAsync(cancellationToken);
                if (verified)
                {
                    foreach (var issue in ownershipIssues) { issue.Status = IssueStatus.Resolved; issue.ResolutionNote = "تطابق نام در بازبینی خودکار تأیید شد."; issue.ResolvedAtUtc = DateTimeOffset.UtcNow; }
                    if (ownershipIssues.Count > 0) { message.ProcessingStatus = ProcessingStatus.Processed; message.LastError = null; }
                }
                else
                {
                    var reason = $"نام صاحب حساب اعلام‌شده توسط بانک با نام پذیرنده مطابقت ندارد. نام بانک: {result.AccountHolderName}؛ نام پذیرنده: {expectedName}.";
                    message.ProcessingStatus = ProcessingStatus.ReconciliationRequired; message.LastError = reason;
                    if (ownershipIssues.Count == 0)
                        db.ReconciliationIssues.Add(new ReconciliationIssue { CampaignId = account.CampaignId, InboundMessageId = message.Id, SenderMobile = message.SenderMobile, ExtractedIban = account.Iban, IssueType = IssueType.AccountHolderNameMismatch, ResolutionNote = reason });
                    else foreach (var issue in ownershipIssues) issue.ResolutionNote = reason;
                }
                await db.SaveChangesAsync(cancellationToken);
            },
            verifySucceeded: cancellationToken => db.SecondaryBankAccounts.AnyAsync(x => x.Id == account.Id
                && x.InboundMessageId == account.InboundMessageId && x.Iban == account.Iban && x.OwnershipVerified == verified,
                cancellationToken),
            cancellationToken: token);
    }
}
