using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmsPanel.Api.Services;

public sealed class DayCardToIbanService(IbanService ibans) : IDisposable
{
    private const string FormUrl = "https://www.day24.ir/sheba";
    private const string AjaxUrl = "https://www.day24.ir/inc/ajax.ashx";
    private static readonly Regex CsrfPattern = new("<input[^>]+name=[\"']csrf[\"'][^>]+value=[\"'](?<value>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, ConversionSession> sessions = new();

    public async Task<DayConversionStart> StartAsync(string owner, string cardNumber, CancellationToken token)
    {
        CleanupExpired();
        if (!cardNumber.StartsWith("502938", StringComparison.Ordinal)) throw new InvalidOperationException("این سرویس فقط کارت‌های بانک دی را پشتیبانی می‌کند.");
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SmsPanel", "1.0"));
        try
        {
            var html = await client.GetStringAsync(FormUrl, token);
            var csrf = WebUtility.HtmlDecode(CsrfPattern.Match(html).Groups["value"].Value);
            if (string.IsNullOrWhiteSpace(csrf)) throw new InvalidOperationException("نشست امن فرم بانک دی دریافت نشد.");
            using var captchaResponse = await client.GetAsync(AjaxUrl + "?action=captcha&" + Guid.NewGuid().ToString("N"), token);
            captchaResponse.EnsureSuccessStatusCode();
            var captchaBytes = await captchaResponse.Content.ReadAsByteArrayAsync(token);
            var captchaMediaType = captchaResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var sessionId = Guid.NewGuid().ToString("N");
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            sessions[sessionId] = new ConversionSession(owner, cardNumber, csrf, client, expiresAt);
            return new DayConversionStart(sessionId, $"data:{captchaMediaType};base64," + Convert.ToBase64String(captchaBytes), expiresAt);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<BankCardConversionResult> CompleteAsync(string owner, string sessionId, string captcha, CancellationToken token)
    {
        if (!sessions.TryRemove(sessionId, out var session) || session.ExpiresAtUtc < DateTimeOffset.UtcNow || !string.Equals(session.Owner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException("نشست تبدیل منقضی شده است؛ تصویر کد امنیتی جدید بگیرید.");
        using (session.Client)
        {
            using var captchaResponse = await session.Client.PostAsync(AjaxUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "recaptcha", ["recaptcha"] = captcha.Trim() }), token);
            captchaResponse.EnsureSuccessStatusCode();
            var captchaJson = await captchaResponse.Content.ReadAsStringAsync(token);
            try
            {
                using var document = JsonDocument.Parse(captchaJson);
                if (!document.RootElement.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("کد امنیتی نادرست یا منقضی است؛ دوباره تلاش کنید.");
            }
            catch (JsonException) { throw new InvalidOperationException("پاسخ اعتبارسنجی کد امنیتی بانک دی قابل خواندن نیست."); }

            var fields = new Dictionary<string, string>
            {
                ["espritaction"] = "submitform", ["duration"] = "1", ["relationcode"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                ["csrf"] = session.Csrf, ["fund_number"] = string.Empty, ["card_number"] = session.CardNumber,
                ["shaba_code"] = string.Empty, ["captcha_2"] = captcha.Trim(), ["button_3"] = string.Empty
            };
            using var response = await session.Client.PostAsync(FormUrl, new FormUrlEncodedContent(fields), token);
            response.EnsureSuccessStatusCode();
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(token));
            var candidates = ibans.Extract(html).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 1) return new BankCardConversionResult(candidates[0], BankAccountHolderNameExtractor.Extract(html));
            var plainText = VisibleText(html);
            var bankError = ExtractBankError(plainText);
            if (bankError is not null) throw new InvalidOperationException("پیام بانک دی: " + bankError);
            throw new InvalidOperationException("بانک دی شماره شبا برنگرداند؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
        }
    }

    private static string VisibleText(string html)
    {
        var withoutCode = Regex.Replace(html, @"<!--.*?-->|<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(withoutCode, "<[^>]+>", " ")), @"\s+", " ").Trim();
    }

    private static string? ExtractBankError(string plainText)
    {
        var match = Regex.Match(plainText, @"(?<message>[^.!؟]{0,100}(?:نامعتبر|اشتباه|یافت نشد|وجود ندارد|امکان.*نیست)[^.!؟]{0,100})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["message"].Value.Trim() : null;
    }

    private void CleanupExpired()
    {
        foreach (var entry in sessions.Where(entry => entry.Value.ExpiresAtUtc < DateTimeOffset.UtcNow).ToList())
            if (sessions.TryRemove(entry.Key, out var expired)) expired.Client.Dispose();
    }

    public void Dispose() { foreach (var session in sessions.Values) session.Client.Dispose(); sessions.Clear(); }
    private sealed record ConversionSession(string Owner, string CardNumber, string Csrf, HttpClient Client, DateTimeOffset ExpiresAtUtc);
}

public sealed record DayConversionStart(string SessionId, string CaptchaImage, DateTimeOffset ExpiresAtUtc);
