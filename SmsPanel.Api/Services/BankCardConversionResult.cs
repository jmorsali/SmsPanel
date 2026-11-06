using System.Net;
using System.Text.RegularExpressions;

namespace SmsPanel.Api.Services;

public sealed record BankCardConversionResult(string Iban, string? AccountHolderName);

public static partial class BankAccountHolderNameExtractor
{
    [GeneratedRegex(@"(?:نام\s*(?:و\s*نام\s*خانوادگی)?\s*(?:صاحب|دارنده)?\s*(?:حساب|کارت)?|صاحب\s*حساب)\s*[:：]?\s*(?<name>[\u0600-\u06FF][\u0600-\u06FF\s‌]{2,80}?)(?=\s*(?:شماره\s*(?:حساب|شبا|کارت)|شبا|بانک|$))", RegexOptions.IgnoreCase)]
    private static partial Regex HolderPattern();

    public static string? Extract(string html)
    {
        var withoutCode = Regex.Replace(html, @"<!--.*?-->|<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(withoutCode, "<[^>]+>", " ")), @"\s+", " ").Trim();
        var match = HolderPattern().Match(text);
        return match.Success ? Regex.Replace(match.Groups["name"].Value, @"\s+", " ").Trim() : null;
    }
}
