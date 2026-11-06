using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SmsPanel.Api.Services;

public sealed class SamanCardToIbanService(IbanService ibans) : IDisposable
{
    private const string StartUrl = "https://www.sb24.com/portal/home/?286147/%D9%85%D8%AD%D8%A7%D8%B3%D8%A8%D9%87-%D8%B4%D8%A8%D8%A7=";
    private const string SubmitUrl = "https://www.sb24.com/e-services/assistant/sheba";
    private const string Prefix = "WebPart_81a3276a_6eef_40c6_b701_a58a418f9a91$EF0$FormWizard$";
    private const string ChoiceName = Prefix + "e2404e03_e382_4d61_90c1_3f7953eca2c4";
    private const string CaptchaName = Prefix + "CaptchaControl$InputTextBox";
    private const string SubmitName = Prefix + "FinishNavigationTemplateContainerID$FinishCompleteButton";
    private static readonly Regex InputPattern = new("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttributePattern = new("(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*[\"'](?<value>.*?)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CaptchaPattern = new("<img\\s+src=[\"'](?<src>[^\"']*CaptchaImage\\.ashx[^\"']*)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, ConversionSession> sessions = new();

    public async Task<SamanConversionStart> StartAsync(string owner, string cardNumber, CancellationToken token)
    {
        CleanupExpired();
        if (!cardNumber.StartsWith("621986", StringComparison.Ordinal)) throw new InvalidOperationException("این سرویس فقط کارت‌های بانک سامان را پشتیبانی می‌کند.");
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SmsPanel", "1.0"));
        try
        {
            var initialHtml = await client.GetStringAsync(StartUrl, token);
            var fields = HiddenFields(initialHtml);
            var cardChoice = ParseInputs(initialHtml).FirstOrDefault(input => input.Name == ChoiceName && input.Id.EndsWith("_1", StringComparison.Ordinal));
            if (cardChoice is null) throw new InvalidOperationException("گزینه تبدیل کارت به شبا در سایت بانک پیدا نشد.");
            fields["__EVENTTARGET"] = ChoiceName + "$1";
            fields[ChoiceName] = cardChoice.Value;
            using var choiceResponse = await client.PostAsync(SubmitUrl, new FormUrlEncodedContent(fields), token);
            choiceResponse.EnsureSuccessStatusCode();
            var formHtml = await choiceResponse.Content.ReadAsStringAsync(token);
            if (formHtml.Contains("بروز خطا در سیستم", StringComparison.Ordinal)) throw new InvalidOperationException("سرویس تبدیل شبا در بانک سامان موقتاً خطا دارد.");
            var inputs = ParseInputs(formHtml);
            var selectedCardChoice = inputs.FirstOrDefault(input => input.Name == ChoiceName && input.Id.EndsWith("_1", StringComparison.Ordinal));
            if (selectedCardChoice is null) throw new InvalidOperationException("وضعیت فرم تبدیل کارت بانک دریافت نشد.");
            var cardFields = inputs.Where(input => input.Type == "text" && input.Name.StartsWith(Prefix, StringComparison.Ordinal) && input.Name != CaptchaName).Select(input => input.Name).Take(4).ToArray();
            if (cardFields.Length != 4) throw new InvalidOperationException("ساختار فرم تبدیل کارت بانک تغییر کرده است.");
            var captchaPath = WebUtility.HtmlDecode(CaptchaPattern.Match(formHtml).Groups["src"].Value);
            if (string.IsNullOrWhiteSpace(captchaPath)) throw new InvalidOperationException("تصویر کد امنیتی بانک دریافت نشد.");
            var captchaUrl = new Uri(new Uri(SubmitUrl), captchaPath);
            using var captchaResponse = await client.GetAsync(captchaUrl, token);
            captchaResponse.EnsureSuccessStatusCode();
            var captchaBytes = await captchaResponse.Content.ReadAsByteArrayAsync(token);
            var captchaMediaType = captchaResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var sessionId = Guid.NewGuid().ToString("N");
            var submissionFields = HiddenFields(formHtml);
            submissionFields[ChoiceName] = selectedCardChoice.Value;
            sessions[sessionId] = new ConversionSession(owner, cardNumber, client, submissionFields, cardFields, DateTimeOffset.UtcNow.AddMinutes(5));
            return new SamanConversionStart(sessionId, $"data:{captchaMediaType};base64," + Convert.ToBase64String(captchaBytes), DateTimeOffset.UtcNow.AddMinutes(5));
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
            var fields = session.Fields;
            fields["__EVENTTARGET"] = string.Empty;
            for (var index = 0; index < 4; index++) fields[session.CardFields[index]] = session.CardNumber.Substring(index * 4, 4);
            fields[CaptchaName] = captcha.Trim();
            fields[SubmitName] = "مشاهده";
            using var response = await session.Client.PostAsync(SubmitUrl, new FormUrlEncodedContent(fields), token);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(token);
            var decodedHtml = WebUtility.HtmlDecode(html);
            var plainText = VisibleText(html);
            var candidates = ibans.Extract(decodedHtml).Concat(ibans.Extract(plainText)).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 1) return new BankCardConversionResult(candidates[0], BankAccountHolderNameExtractor.Extract(html));
            if (plainText.Contains("کد امنیتی وارد شده", StringComparison.Ordinal) || plainText.Contains("کد امنیتی اشتباه", StringComparison.Ordinal) || plainText.Contains("کد امنیتی صحیح", StringComparison.Ordinal))
                throw new InvalidOperationException("کد امنیتی نادرست یا منقضی است؛ دوباره تلاش کنید.");
            var bankError = ExtractBankError(plainText);
            if (bankError is not null) throw new InvalidOperationException("پیام بانک: " + bankError);
            throw new InvalidOperationException("بانک شماره شبا برنگرداند؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
        }
    }

    private static Dictionary<string, string> HiddenFields(string html) => ParseInputs(html)
        .Where(input => input.Type == "hidden" && !string.IsNullOrWhiteSpace(input.Name))
        .GroupBy(input => input.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

    private static List<FormInput> ParseInputs(string html) => InputPattern.Matches(html).Select(match =>
    {
        var attributes = AttributePattern.Matches(match.Value).ToDictionary(item => item.Groups["name"].Value.ToLowerInvariant(), item => WebUtility.HtmlDecode(item.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
        return new FormInput(attributes.GetValueOrDefault("type", "text").ToLowerInvariant(), attributes.GetValueOrDefault("name", string.Empty), attributes.GetValueOrDefault("id", string.Empty), attributes.GetValueOrDefault("value", string.Empty));
    }).ToList();

    private static string? ExtractBankError(string plainText)
    {
        var compact = Regex.Replace(plainText, @"\s+", " ").Trim();
        var match = Regex.Match(compact, @"(?<message>[^.!؟]{0,120}(?:نامعتبر|یافت نشد|وجود ندارد|امکان.*نیست|خطا)[^.!؟]{0,120})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["message"].Value.Trim() : null;
    }

    private static string VisibleText(string html)
    {
        var withoutCode = Regex.Replace(html, @"<!--.*?-->|<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(withoutCode, "<[^>]+>", " "));
    }

    private void CleanupExpired()
    {
        foreach (var entry in sessions.Where(entry => entry.Value.ExpiresAtUtc < DateTimeOffset.UtcNow).ToList())
            if (sessions.TryRemove(entry.Key, out var expired)) expired.Client.Dispose();
    }

    public void Dispose() { foreach (var session in sessions.Values) session.Client.Dispose(); sessions.Clear(); }
    private sealed record FormInput(string Type, string Name, string Id, string Value);
    private sealed record ConversionSession(string Owner, string CardNumber, HttpClient Client, Dictionary<string, string> Fields, string[] CardFields, DateTimeOffset ExpiresAtUtc);
}

public sealed record SamanConversionStart(string SessionId, string CaptchaImage, DateTimeOffset ExpiresAtUtc);
