using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;

namespace SmsPanel.Api.Services;

public sealed class InboundPersonNameResolver(SmsDbContext db)
{
    private static readonly HashSet<string> IgnoredWords = new(StringComparer.Ordinal)
    {
        "سلام", "درود", "با", "احترام", "تشکر", "ممنون", "شماره", "شبا", "حساب", "بنام", "به", "نام", "بانک",
        "ملی", "ملت", "صادرات", "تجارت", "سامان", "پارسیان", "پاسارگاد", "رفاه", "سپه", "دی", "تعاون", "توسعه", "کشاورزی", "مسکن", "آینده", "اقتصاد", "نوین", "شهر", "سینا", "گردشگری", "خاورمیانه"
    };

    public async Task<Person?> FindUniqueAsync(string messageText, CancellationToken token)
    {
        var messageParts = PersonNameMatcher.Parts(messageText).Where(x => !IgnoredWords.Contains(x) && !x.Any(char.IsDigit) && !x.Equals("IR", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (messageParts.Length == 0) return null;
        var people = await db.People.AsNoTracking().Select(x => new { x.Id, x.FirstName, x.LastName }).ToListAsync(token);
        var matches = people.Select(person => new { person.Id, Score = BestScore($"{person.FirstName} {person.LastName}", messageParts) })
            .Where(x => x.Score >= 0.90).OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(2).ToList();
        if (matches.Count == 0 || (matches.Count > 1 && matches[0].Score - matches[1].Score < 0.05)) return null;
        return await db.People.SingleAsync(x => x.Id == matches[0].Id, token);
    }

    public async Task<Person?> FindUniqueByNameAsync(string accountHolderName, CancellationToken token)
    {
        var people = await db.People.AsNoTracking().Select(x => new { x.Id, x.FirstName, x.LastName }).ToListAsync(token);
        var matches = people.Select(person => new { person.Id, Score = PersonNameMatcher.SimilarityScore($"{person.FirstName} {person.LastName}", accountHolderName) })
            .Where(x => x.Score >= 0.90).OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(2).ToList();
        if (matches.Count == 0 || (matches.Count > 1 && matches[0].Score - matches[1].Score < 0.05)) return null;
        return await db.People.SingleAsync(x => x.Id == matches[0].Id, token);
    }

    private static double BestScore(string fullName, string[] messageParts)
    {
        var nameLength = PersonNameMatcher.Parts(fullName).Length;
        if (nameLength == 0) return 0;
        var best = 0d;
        for (var length = Math.Max(1, nameLength - 1); length <= Math.Min(messageParts.Length, nameLength + 1); length++)
            for (var start = 0; start + length <= messageParts.Length; start++)
                best = Math.Max(best, PersonNameMatcher.SimilarityScore(fullName, string.Join(' ', messageParts.Skip(start).Take(length))));
        return best;
    }
}
