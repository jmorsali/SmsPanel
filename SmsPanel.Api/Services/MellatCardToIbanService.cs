using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmsPanel.Api.Services;

public sealed class MellatCardToIbanService(IbanService ibans) : IDisposable
{
    private const string FormUrl = "https://pishkhan.mellat.ir/pishkhan/start";
    private readonly ConcurrentDictionary<string, BrowserSession> sessions = new();

    public async Task<MellatConversionStart> StartAsync(string owner, string cardNumber, CancellationToken token)
    {
        CleanupExpired();
        if (!cardNumber.StartsWith("610433", StringComparison.Ordinal) && !cardNumber.StartsWith("991975", StringComparison.Ordinal))
            throw new InvalidOperationException("این سرویس فقط کارت‌های بانک ملت را پشتیبانی می‌کند.");

        var browserSession = await BrowserSession.StartAsync(owner, cardNumber, token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var result = await browserSession.DevTools.EvaluateAsync("""
                (async () => {
                    if (!document.querySelector('#formShebaFirst')) {
                        if (typeof startPrdcSheba !== 'function') throw new Error('sheba form function not found');
                        startPrdcSheba();
                    }
                    const deadline = Date.now() + 20000;
                    while (Date.now() < deadline && !document.querySelector('#formShebaFirst')) await new Promise(r => setTimeout(r, 100));
                    if (typeof changeShebaTab === 'function') changeShebaTab('PAN_NUM');
                    const image = document.querySelector('#captchaImg');
                    while (Date.now() < deadline && (!image || !image.complete || !image.naturalWidth)) await new Promise(r => setTimeout(r, 100));
                    if (!image?.src) return { image: '', error: 'captcha image not found' };
                    const response = await fetch(image.src, { credentials: 'include', cache: 'no-store' });
                    const blob = await response.blob();
                    const dataUrl = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject; reader.readAsDataURL(blob); });
                    return { image: dataUrl, error: '' };
                })()
                """, timeout.Token);
            var captchaImage = result.GetProperty("image").GetString();
            if (string.IsNullOrWhiteSpace(captchaImage)) throw new InvalidOperationException("تصویر کد امنیتی بانک ملت دریافت نشد.");
            var sessionId = Guid.NewGuid().ToString("N");
            sessions[sessionId] = browserSession;
            return new MellatConversionStart(sessionId, captchaImage, browserSession.ExpiresAtUtc);
        }
        catch
        {
            browserSession.Dispose();
            throw;
        }
    }

    public async Task<BankCardConversionResult> CompleteAsync(string owner, string sessionId, string captcha, CancellationToken token)
    {
        if (!sessions.TryRemove(sessionId, out var session) || session.ExpiresAtUtc < DateTimeOffset.UtcNow || !string.Equals(session.Owner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException("نشست تبدیل منقضی شده است؛ تصویر کد امنیتی جدید بگیرید.");

        using (session)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var cardParts = Enumerable.Range(0, 4).Select(index => session.CardNumber.Substring(index * 4, 4)).ToArray();
            var expression = $$"""
                (async () => {
                    const setValue = (selector, value) => { const input = document.querySelector(selector); if (!input) throw new Error(selector + ' not found'); input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); };
                    if (typeof changeShebaTab === 'function') changeShebaTab('PAN_NUM');
                    setValue('#panNumber1', {{JsonSerializer.Serialize(cardParts[0])}});
                    setValue('#panNumber2', {{JsonSerializer.Serialize(cardParts[1])}});
                    setValue('#panNumber3', {{JsonSerializer.Serialize(cardParts[2])}});
                    setValue('#panNumber4', {{JsonSerializer.Serialize(cardParts[3])}});
                    setValue('#captcha', {{JsonSerializer.Serialize(captcha.Trim())}});
                    if (typeof submitAjaxShebaFirst !== 'function') throw new Error('submit function not found');
                    submitAjaxShebaFirst('formShebaFirst');
                    const deadline = Date.now() + 28000;
                    let confirmed = false;
                    while (Date.now() < deadline) {
                        await new Promise(r => setTimeout(r, 150));
                        const content = document.querySelector('#contentId');
                        const text = content?.innerText || '';
                        if (/IR\s*[0-9۰-۹٠-٩]/i.test(text)) return { text, html: content.innerHTML, error: '' };
                        const error = content?.querySelector('.alert-danger:not(.hidden), #hasActionErrors:not(.hidden), #hasActionErrorsClient:not(.hidden)')?.innerText?.trim() || '';
                        if (error) return { text, html: content.innerHTML, error };
                        if (!confirmed && !document.querySelector('#formShebaFirst')) {
                            const buttons = [...content.querySelectorAll('button, input[type=button], input[type=submit], a')];
                            const confirm = buttons.find(element => /تایید|تأیید|دریافت شماره شبا|ادامه/.test((element.innerText || element.value || '').trim()) && !/بازگشت/.test((element.innerText || element.value || '').trim()));
                            if (confirm) { confirmed = true; confirm.click(); }
                        }
                    }
                    const content = document.querySelector('#contentId');
                    return { text: content?.innerText || '', html: content?.innerHTML || '', error: '' };
                })()
                """;
            var result = await session.DevTools.EvaluateAsync(expression, timeout.Token);
            var text = result.GetProperty("text").GetString() ?? string.Empty;
            var html = result.GetProperty("html").GetString() ?? string.Empty;
            var error = result.GetProperty("error").GetString() ?? string.Empty;
            var candidates = ibans.Extract(text).Concat(ibans.ExtractCorrectable(text)).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 1) return new BankCardConversionResult(candidates[0], BankAccountHolderNameExtractor.Extract(html));
            if (error.Contains("امنیتی", StringComparison.Ordinal) || error.Contains("عبارت", StringComparison.Ordinal) && error.Contains("صحیح", StringComparison.Ordinal))
                throw new InvalidOperationException("کد امنیتی نادرست یا منقضی است؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("پیام بانک ملت: " + Regex.Replace(error, @"\s+", " ").Trim());
            throw new InvalidOperationException("بانک ملت شماره شبا برنگرداند؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
        }
    }

    private void CleanupExpired()
    {
        foreach (var item in sessions.Where(item => item.Value.ExpiresAtUtc < DateTimeOffset.UtcNow).ToList())
            if (sessions.TryRemove(item.Key, out var expired)) expired.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
    }

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
        {
            Owner = owner; CardNumber = cardNumber; this.browser = browser; this.socket = socket; this.profilePath = profilePath; DevTools = new DevToolsClient(socket);
        }

        public static async Task<BrowserSession> StartAsync(string owner, string cardNumber, CancellationToken token)
        {
            var profile = Path.Combine(Path.GetTempPath(), "sms-panel-mellat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            Process? process = null;
            ClientWebSocket? socket = null;
            try
            {
                var start = new ProcessStartInfo(FindBrowser()) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profile, FormUrl }) start.ArgumentList.Add(argument);
                process = Process.Start(start) ?? throw new InvalidOperationException("مرورگر مخفی بانک ملت اجرا نشد.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var port = await WaitForPortAsync(profile, process, timeout.Token);
                var uri = await WaitForPageAsync(port, timeout.Token);
                socket = new ClientWebSocket();
                await socket.ConnectAsync(uri, timeout.Token);
                var session = new BrowserSession(owner, cardNumber, process, socket, profile);
                await session.DevTools.WaitForDocumentAsync(timeout.Token);
                return session;
            }
            catch
            {
                socket?.Dispose();
                if (process is { HasExited: false }) process.Kill(true);
                process?.Dispose();
                TryDelete(profile);
                throw;
            }
        }

        private static string FindBrowser()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return new[] { Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(x86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(x86, "Microsoft", "Edge", "Application", "msedge.exe") }.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("مرورگر کروم یا اج روی سرور نصب نیست.");
        }

        private static async Task<int> WaitForPortAsync(string profile, Process process, CancellationToken token)
        {
            var file = Path.Combine(profile, "DevToolsActivePort");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("مرورگر مخفی بانک ملت بسته شد.");
                if (File.Exists(file)) { var lines = await File.ReadAllLinesAsync(file, token); if (lines.Length > 0 && int.TryParse(lines[0], out var port)) return port; }
                await Task.Delay(100, token);
            }
        }

        private static async Task<Uri> WaitForPageAsync(int port, CancellationToken token)
        {
            using var client = new HttpClient();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var document = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/json/list", token));
                    var page = document.RootElement.EnumerateArray().FirstOrDefault(item => item.GetProperty("type").GetString() == "page");
                    if (page.ValueKind != JsonValueKind.Undefined) return new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!);
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, token);
            }
        }

        public void Dispose()
        {
            socket.Dispose();
            if (!browser.HasExited) { try { browser.Kill(true); browser.WaitForExit(3000); } catch (InvalidOperationException) { } }
            browser.Dispose();
            TryDelete(profilePath);
        }

        private static void TryDelete(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class DevToolsClient(ClientWebSocket socket)
    {
        private int id;

        public async Task WaitForDocumentAsync(CancellationToken token)
        {
            while (true)
            {
                var state = await EvaluateAsync("({ ready: document.readyState, url: location.href })", token);
                if (state.GetProperty("ready").GetString() == "complete" && state.GetProperty("url").GetString()?.StartsWith(FormUrl, StringComparison.OrdinalIgnoreCase) == true) return;
                await Task.Delay(100, token);
            }
        }

        public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
        {
            var response = await SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
            if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException("فرمان مرورگر بانک ملت اجرا نشد: " + error.GetProperty("message").GetString());
            var outer = response.GetProperty("result");
            if (outer.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("اجرای فرم بانک ملت در مرورگر ناموفق بود.");
            var result = outer.GetProperty("result");
            if (!result.TryGetProperty("value", out var value)) throw new InvalidOperationException("صفحه بانک ملت نتیجه‌ای برنگرداند.");
            return value.Clone();
        }

        private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token)
        {
            var commandId = Interlocked.Increment(ref id);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = commandId, method, @params = parameters });
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            var buffer = new byte[16384];
            while (true)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult read;
                do
                {
                    read = await socket.ReceiveAsync(buffer, token);
                    if (read.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("ارتباط مرورگر بانک ملت بسته شد.");
                    stream.Write(buffer, 0, read.Count);
                } while (!read.EndOfMessage);
                using var document = JsonDocument.Parse(stream.ToArray());
                if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == commandId) return document.RootElement.Clone();
            }
        }
    }
}

public sealed record MellatConversionStart(string SessionId, string CaptchaImage, DateTimeOffset ExpiresAtUtc);
