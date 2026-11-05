using System.Text.RegularExpressions;

namespace SmsPanel.Api.Services;

public sealed partial class PersianPersonNameParser
{
    private static readonly HashSet<string> CompoundFirstNames = new(StringComparer.Ordinal)
    {
        "امیر حسین", "امیر علی", "امیر محمد", "امیر رضا",
        "سید احمد", "سید حسن", "سید حسین", "سید رضا", "سید علی", "سید محمد", "سید مهدی", "سید مجتبی", "سید جواد",
        "سیده زهرا", "سیده فاطمه", "سیده مریم", "سیده معصومه",
        "غلام حسین", "غلام رضا", "غلام علی",
        "محمد امین", "محمد باقر", "محمد جواد", "محمد حسن", "محمد حسین", "محمد رضا", "محمد علی", "محمد مهدی", "محمد هادی",
        "روح الله", "نور الله"
    };

    private static readonly string[] Honorifics =
    [
        "جناب آقای ", "سرکار خانم ", "جناب ", "آقای ", "خانم "
    ];

    public ParsedPersonName Parse(string? fullName)
    {
        var normalized = Normalize(fullName);
        if (normalized.Length == 0) return new(string.Empty, string.Empty);

        foreach (var honorific in Honorifics)
        {
            if (normalized.StartsWith(honorific, StringComparison.Ordinal))
            {
                normalized = normalized[honorific.Length..].Trim();
                break;
            }
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return new(parts[0], string.Empty);

        var firstNameLength = parts.Length > 2 && CompoundFirstNames.Contains($"{parts[0]} {parts[1]}") ? 2 : 1;
        return new(
            string.Join(' ', parts.Take(firstNameLength)),
            string.Join(' ', parts.Skip(firstNameLength)));
    }

    private static string Normalize(string? value) =>
        Whitespace().Replace((value ?? string.Empty).Trim().Replace('ي', 'ی').Replace('ك', 'ک'), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed record ParsedPersonName(string FirstName, string LastName);
