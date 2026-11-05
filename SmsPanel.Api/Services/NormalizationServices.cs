using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
namespace SmsPanel.Api.Services;

public sealed partial class IbanService
{
    [GeneratedRegex(@"IR(?:[\s\u200C\u200E\u200F\-–—_.:/]*\d){24}|(?<!\d)\d{4}\s*[\-–—]\s*\d{4}\s*[\-–—]\s*\d{4}\s*[\-–—]\s*\d{4}\s*[\-–—]\s*\d{8}(?!\d)|(?<!\d)\d{24}(?!\d)", RegexOptions.IgnoreCase)] private static partial Regex ExtractPattern();
    [GeneratedRegex(@"IR(?:[\s\u200C\u200E\u200F\-–—_.:/]*\d){22,23}(?![\s\u200C\u200E\u200F\-–—_.:/]*\d)|(?<!\d)\d{22,23}(?!\d)", RegexOptions.IgnoreCase)] private static partial Regex IncompletePattern();
    [GeneratedRegex(@"IR[0-9A-Z]{24}(?![0-9A-Z])", RegexOptions.IgnoreCase)] private static partial Regex CorrectablePattern();
    [GeneratedRegex(@"(?<!\d)\d{6}(?:\s*[/\-–—]\s*\d{6}){3}(?!\d)")] private static partial Regex GroupedBodyPattern();
    [GeneratedRegex(@"(?<!\d)\d{25}(?!\d)")] private static partial Regex ExtraDigitPattern();
    [GeneratedRegex(@"^IR\d{24}$", RegexOptions.IgnoreCase)] private static partial Regex CanonicalPattern();
    public IReadOnlyList<string> Extract(string input) => ExtractPattern().Matches(NormalizeDigits(input))
        .Select(match => "IR" + new string((match.Value.StartsWith("IR", StringComparison.OrdinalIgnoreCase) ? match.Value[2..] : match.Value).Where(char.IsDigit).ToArray()))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ExtractIncomplete(string input) => IncompletePattern().Matches(NormalizeDigits(input))
        .Select(match => "IR" + new string((match.Value.StartsWith("IR", StringComparison.OrdinalIgnoreCase) ? match.Value[2..] : match.Value).Where(char.IsDigit).ToArray()))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ExtractCorrectable(string input)
    {
        var corrected = new List<string>();
        var normalizedInput = NormalizeDigits(input);
        foreach (Match match in CorrectablePattern().Matches(normalizedInput))
        {
            var body = match.Value[2..].ToCharArray();
            var invalidPositions = body.Select((value, index) => new { value, index }).Where(x => !char.IsDigit(x.value)).ToList();
            if (invalidPositions.Count != 1) continue;
            foreach (var digit in "0123456789") { body[invalidPositions[0].index] = digit; var candidate = "IR" + new string(body); if (IsValid(candidate)) corrected.Add(candidate); }
        }

        foreach (Match match in GroupedBodyPattern().Matches(normalizedInput))
        {
            var candidate = "IR" + new string(match.Value.Where(char.IsDigit).ToArray());
            if (IsValid(candidate)) corrected.Add(candidate);
        }

        foreach (Match match in ExtraDigitPattern().Matches(normalizedInput))
        {
            var digits = match.Value;
            var candidates = Enumerable.Range(0, digits.Length)
                .Select(index => "IR" + digits.Remove(index, 1))
                .Where(IsValid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 1) corrected.Add(candidates[0]);
        }

        foreach (var incomplete in ExtractIncomplete(normalizedInput).Where(value => value.Length == 25))
        {
            var digits = incomplete[2..];
            var candidates = Enumerable.Range(0, digits.Length + 1)
                .SelectMany(index => "0123456789".Select(digit => "IR" + digits.Insert(index, digit.ToString())))
                .Where(IsValid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 1) corrected.Add(candidates[0]);
        }
        return corrected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public bool IsValid(string iban) { var normalized = NormalizeDigits(iban).Trim(); if (!normalized.StartsWith("IR", StringComparison.OrdinalIgnoreCase)) return false; var n = "IR" + new string(normalized.Skip(2).Where(char.IsDigit).ToArray()); if (n.Length != 26 || !CanonicalPattern().IsMatch(n)) return false; var s = n[4..] + "1827" + n[2..4]; BigInteger r = 0; foreach (var d in s) r = (r * 10 + d - '0') % 97; return r == 1; }
    internal static string NormalizeDigits(string value) { var b = new StringBuilder(); foreach (var c in value) b.Append(c switch { >= '۰' and <= '۹' => (char)('0' + c - '۰'), >= '٠' and <= '٩' => (char)('0' + c - '٠'), _ => c }); return b.ToString(); }
}
public sealed class MobileNormalizer { public string Normalize(string value) { var m = new string(IbanService.NormalizeDigits(value).Where(char.IsDigit).ToArray()); if (m.StartsWith("0098")) m = "0" + m[4..]; else if (m.StartsWith("98")) m = "0" + m[2..]; else if (m.Length == 10 && m.StartsWith('9')) m = "0" + m; if (m.Length != 11 || !m.StartsWith("09")) throw new ArgumentException("شماره موبایل نامعتبر است."); return m; } }
