using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
namespace SmsPanel.Api.Services;

public sealed partial class IbanService
{
    [GeneratedRegex(@"IR\d{24}", RegexOptions.IgnoreCase)] private static partial Regex Pattern();
    public IReadOnlyList<string> Extract(string input) { var n = NormalizeDigits(input).Replace(" ", "").Replace("-", "").ToUpperInvariant(); return Pattern().Matches(n).Select(x => x.Value).Distinct().ToList(); }
    public bool IsValid(string iban) { var n = iban.Replace(" ", "").ToUpperInvariant(); if (n.Length != 26 || !Pattern().IsMatch(n)) return false; var s = n[4..] + "1827" + n[2..4]; BigInteger r = 0; foreach (var d in s) r = (r * 10 + d - '0') % 97; return r == 1; }
    internal static string NormalizeDigits(string value) { var b = new StringBuilder(); foreach (var c in value) b.Append(c switch { >= '۰' and <= '۹' => (char)('0' + c - '۰'), >= '٠' and <= '٩' => (char)('0' + c - '٠'), _ => c }); return b.ToString(); }
}
public sealed class MobileNormalizer { public string Normalize(string value) { var m = new string(IbanService.NormalizeDigits(value).Where(char.IsDigit).ToArray()); if (m.StartsWith("0098")) m = "0" + m[4..]; else if (m.StartsWith("98")) m = "0" + m[2..]; else if (m.Length == 10 && m.StartsWith('9')) m = "0" + m; if (m.Length != 11 || !m.StartsWith("09")) throw new ArgumentException("شماره موبایل نامعتبر است."); return m; } }
