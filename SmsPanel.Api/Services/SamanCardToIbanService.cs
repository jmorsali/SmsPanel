using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmsPanel.Api.Services;

public sealed class SamanCardToIbanService(IbanService ibans) : IDisposable
{
    private const string FormUrl = "https://www.sb24.com/portal/home/?286147/%D9%85%D8%AD%D8%A7%D8%B3%D8%A8%D9%87-%D8%B4%D8%A8%D8%A7=";
    private const string WizardPrefix = "WebPart_81a3276a_6eef_40c6_b701_a58a418f9a91_EF0_FormWizard";
    private readonly ConcurrentDictionary<string, BrowserSession> sessions = new();

    public async Task<SamanConversionStart> StartAsync(string owner, string cardNumber, CancellationToken token)
    {
        CleanupExpired();
        if (!cardNumber.StartsWith("621986", StringComparison.Ordinal))
            throw new InvalidOperationException("این سرویس فقط کارت‌های بانک سامان را پشتیبانی می‌کند.");

        var session = await BrowserSession.StartAsync(owner, cardNumber, token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var selected = await session.DevTools.EvaluateAsync($$"""
                (() => {
                    const prefix = {{JsonSerializer.Serialize(WizardPrefix)}};
                    const radio = document.querySelector(`input[type=radio][id^="${prefix}"][id$="_1"]`);
                    if (!radio) return false;
                    if (!radio.checked) setTimeout(() => radio.click(), 0);
                    return true;
                })()
                """, timeout.Token);
            if (!selected.GetBoolean()) throw new InvalidOperationException("گزینه تبدیل کارت به شبا در سایت بانک پیدا نشد.");
            await Task.Delay(700, timeout.Token);
            await session.DevTools.WaitForDocumentAsync(timeout.Token);

            var result = await session.DevTools.EvaluateAsync($$"""
                (async () => {
                    const prefix = {{JsonSerializer.Serialize(WizardPrefix)}};
                    const deadline = Date.now() + 30000;
                    while (Date.now() < deadline) {
                        await new Promise(resolve => setTimeout(resolve, 150));
                        const text = document.body?.innerText || '';
                        if (/بروز خطا در سیستم|خطا در عملیات/.test(text)) return { image: '', error: 'bank' };
                        const wizard = document.querySelector(`[id^="${prefix}"]`);
                        const image = wizard?.querySelector('img[src*="CaptchaImage.ashx"]');
                        const fields = wizard ? [...wizard.querySelectorAll('input[type=text]')].filter(x => !/CaptchaControl/.test(x.id) && x.offsetParent !== null) : [];
                        if (image?.src && image.complete && image.naturalWidth && fields.length >= 4) {
                            const response = await fetch(image.src, { credentials: 'include', cache: 'no-store' });
                            const blob = await response.blob();
                            const dataUrl = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject; reader.readAsDataURL(blob); });
                            return { image: dataUrl, error: '' };
                        }
                    }
                    return { image: '', error: 'captcha' };
                })()
                """, timeout.Token);

            var image = result.GetProperty("image").GetString();
            if (string.IsNullOrWhiteSpace(image))
                throw new InvalidOperationException(result.GetProperty("error").GetString() == "bank"
                    ? "سرویس تبدیل کارت بانک سامان در سایت بانک موقتاً خطا دارد."
                    : "تصویر کد امنیتی بانک سامان دریافت نشد.");

            var id = Guid.NewGuid().ToString("N");
            sessions[id] = session;
            return new SamanConversionStart(id, image, session.ExpiresAtUtc);
        }
        catch { session.Dispose(); throw; }
    }

    public async Task<BankCardConversionResult> CompleteAsync(string owner, string sessionId, string captcha, CancellationToken token)
    {
        if (!sessions.TryRemove(sessionId, out var session) || session.ExpiresAtUtc < DateTimeOffset.UtcNow || !string.Equals(session.Owner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException("نشست تبدیل منقضی شده است؛ تصویر کد امنیتی جدید بگیرید.");

        using (session)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var parts = Enumerable.Range(0, 4).Select(i => session.CardNumber.Substring(i * 4, 4)).ToArray();
            var submitted = await session.DevTools.EvaluateAsync($$"""
                (() => {
                    const prefix = {{JsonSerializer.Serialize(WizardPrefix)}};
                    const wizard = document.querySelector(`[id^="${prefix}"]`);
                    if (!wizard) return { submitted: false, error: 'فرم بانک در دسترس نیست.' };
                    const setValue = (input, value) => { input.focus(); input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); };
                    const fields = [...wizard.querySelectorAll('input[type=text]')].filter(x => !/CaptchaControl/.test(x.id) && x.offsetParent !== null).slice(0, 4);
                    const captcha = wizard.querySelector('input[type=text][id*="CaptchaControl_InputTextBox"]');
                    const submit = wizard.querySelector('input[id$="FinishCompleteButton"], button[id$="FinishCompleteButton"]');
                    if (fields.length !== 4 || !captcha || !submit) return { submitted: false, error: 'ساختار فرم بانک تغییر کرده است.' };
                    const values = {{JsonSerializer.Serialize(parts)}};
                    fields.forEach((input, i) => setValue(input, values[i]));
                    setValue(captcha, {{JsonSerializer.Serialize(captcha.Trim())}});
                    setTimeout(() => submit.click(), 0);
                    return { submitted: true, error: '' };
                })()
                """, timeout.Token);
            if (!submitted.GetProperty("submitted").GetBoolean())
                throw new InvalidOperationException(submitted.GetProperty("error").GetString() ?? "فرم بانک سامان قابل ارسال نیست.");

            await Task.Delay(700, timeout.Token);
            await session.DevTools.WaitForDocumentAsync(timeout.Token);
            var result = await session.DevTools.EvaluateAsync("""
                (async () => {
                    const deadline = Date.now() + 30000;
                    while (Date.now() < deadline) {
                        await new Promise(resolve => setTimeout(resolve, 180));
                        const text = document.body?.innerText || '';
                        if (/IR\s*[0-9۰-۹٠-٩]/i.test(text)) return { text, html: document.body.innerHTML, error: '' };
                        if (/کد امنیتی[^\n]{0,80}(?:اشتباه|نادرست|صحیح نیست)/.test(text)) return { text, html: document.body.innerHTML, error: 'captcha' };
                        if (/بروز خطا در سیستم|خطا در عملیات/.test(text)) return { text, html: document.body.innerHTML, error: 'خطا در عملیات بانک سامان' };
                    }
                    return { text: document.body?.innerText || '', html: document.body?.innerHTML || '', error: '' };
                })()
                """, timeout.Token);

            var text = result.GetProperty("text").GetString() ?? string.Empty;
            var html = result.GetProperty("html").GetString() ?? string.Empty;
            var error = result.GetProperty("error").GetString() ?? string.Empty;
            var candidates = ibans.Extract(text).Concat(ibans.ExtractCorrectable(text)).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 1) return new BankCardConversionResult(candidates[0], BankAccountHolderNameExtractor.Extract(html));
            if (error == "captcha" || text.Contains("کد امنیتی", StringComparison.Ordinal) && Regex.IsMatch(text, "اشتباه|نادرست|صحیح نیست"))
                throw new InvalidOperationException("کد امنیتی نادرست یا منقضی است؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("پیام بانک سامان: " + error);
            throw new InvalidOperationException("بانک سامان شماره شبا برنگرداند؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
        }
    }

    private void CleanupExpired() { foreach (var item in sessions.Where(x => x.Value.ExpiresAtUtc < DateTimeOffset.UtcNow).ToList()) if (sessions.TryRemove(item.Key, out var expired)) expired.Dispose(); }
    public void Dispose() { foreach (var session in sessions.Values) session.Dispose(); sessions.Clear(); }

    private sealed class BrowserSession : IDisposable
    {
        private readonly Process browser;
        private readonly ClientWebSocket socket;
        private readonly string profilePath;
        public string Owner { get; }
        public string CardNumber { get; }
        public DateTimeOffset ExpiresAtUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public DevToolsClient DevTools { get; }

        private BrowserSession(string owner, string cardNumber, Process browser, ClientWebSocket socket, string profilePath)
        { Owner = owner; CardNumber = cardNumber; this.browser = browser; this.socket = socket; this.profilePath = profilePath; DevTools = new DevToolsClient(socket); }

        public static async Task<BrowserSession> StartAsync(string owner, string cardNumber, CancellationToken token)
        {
            var profile = Path.Combine(Path.GetTempPath(), "sms-panel-saman-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            Process? process = null; ClientWebSocket? socket = null;
            try
            {
                var start = new ProcessStartInfo(FindBrowser()) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var arg in new[] { "--headless=new", "--disable-gpu", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profile, FormUrl }) start.ArgumentList.Add(arg);
                process = Process.Start(start) ?? throw new InvalidOperationException("مرورگر مخفی بانک سامان اجرا نشد.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
                var port = await WaitForPortAsync(profile, process, timeout.Token);
                var uri = await WaitForPageAsync(port, timeout.Token);
                socket = new ClientWebSocket(); await socket.ConnectAsync(uri, timeout.Token);
                var session = new BrowserSession(owner, cardNumber, process, socket, profile);
                await session.DevTools.WaitForDocumentAsync(timeout.Token);
                return session;
            }
            catch { socket?.Dispose(); if (process is { HasExited: false }) process.Kill(true); process?.Dispose(); TryDelete(profile); throw; }
        }

        private static string FindBrowser()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return new[] { Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(x86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(x86, "Microsoft", "Edge", "Application", "msedge.exe") }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("مرورگر کروم یا اج روی سرور نصب نیست.");
        }
        private static async Task<int> WaitForPortAsync(string profile, Process process, CancellationToken token) { var file = Path.Combine(profile, "DevToolsActivePort"); while (true) { token.ThrowIfCancellationRequested(); if (process.HasExited) throw new InvalidOperationException("مرورگر مخفی بانک سامان بسته شد."); if (File.Exists(file)) { var lines = await File.ReadAllLinesAsync(file, token); if (lines.Length > 0 && int.TryParse(lines[0], out var port)) return port; } await Task.Delay(100, token); } }
        private static async Task<Uri> WaitForPageAsync(int port, CancellationToken token) { using var client = new HttpClient(); while (true) { token.ThrowIfCancellationRequested(); try { using var json = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/json/list", token)); var page = json.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("type").GetString() == "page"); if (page.ValueKind != JsonValueKind.Undefined) return new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!); } catch (HttpRequestException) { } await Task.Delay(100, token); } }
        public void Dispose() { socket.Dispose(); if (!browser.HasExited) { try { browser.Kill(true); browser.WaitForExit(3000); } catch (InvalidOperationException) { } } browser.Dispose(); TryDelete(profilePath); }
        private static void TryDelete(string path) { if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return; try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private sealed class DevToolsClient(ClientWebSocket socket)
    {
        private int id;
        public async Task WaitForDocumentAsync(CancellationToken token)
        {
            while (true)
            {
                try
                {
                    var state = await EvaluateAsync("({ ready: document.readyState, url: location.href })", token);
                    if (state.GetProperty("ready").GetString() == "complete" && state.GetProperty("url").GetString()?.StartsWith("https://www.sb24.com/", StringComparison.OrdinalIgnoreCase) == true) return;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("navigated or closed", StringComparison.OrdinalIgnoreCase)) { }
                await Task.Delay(100, token);
            }
        }
        public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
        {
            var response = await SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
            if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException("فرمان مرورگر بانک سامان اجرا نشد: " + error.GetProperty("message").GetString());
            var outer = response.GetProperty("result"); if (outer.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("اجرای فرم بانک سامان در مرورگر ناموفق بود.");
            var result = outer.GetProperty("result"); if (!result.TryGetProperty("value", out var value)) throw new InvalidOperationException("صفحه بانک سامان نتیجه‌ای برنگرداند."); return value.Clone();
        }
        private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token)
        {
            var commandId = Interlocked.Increment(ref id); var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = commandId, method, @params = parameters }); await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); var buffer = new byte[16384];
            while (true) { using var stream = new MemoryStream(); WebSocketReceiveResult read; do { read = await socket.ReceiveAsync(buffer, token); if (read.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("ارتباط مرورگر بانک سامان بسته شد."); stream.Write(buffer, 0, read.Count); } while (!read.EndOfMessage); using var json = JsonDocument.Parse(stream.ToArray()); if (json.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == commandId) return json.RootElement.Clone(); }
        }
    }
}

public sealed record SamanConversionStart(string SessionId, string CaptchaImage, DateTimeOffset ExpiresAtUtc);
