using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace SmsPanel.Api.Services;

public sealed class TejaratCardToIbanService(IbanService ibans) : IDisposable
{
    private const string FormUrl = "https://www.tejaratbank.ir/web_directory/1835-Get-the-sheba-code.html";
    private readonly ConcurrentDictionary<string, BrowserSession> sessions = new();

    public async Task<TejaratConversionStart> StartAsync(string owner, string cardNumber, CancellationToken token)
    {
        CleanupExpired();
        if (!cardNumber.StartsWith("585983", StringComparison.Ordinal) && !cardNumber.StartsWith("627353", StringComparison.Ordinal)) throw new InvalidOperationException("این سرویس فقط کارت‌های بانک تجارت را پشتیبانی می‌کند.");
        var browserSession = await BrowserSession.StartAsync(owner, cardNumber, token);
        try
        {
            JsonElement page = default;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!timeout.IsCancellationRequested)
            {
                page = await browserSession.DevTools.EvaluateAsync("(() => { document.querySelector('#pan_to_iban')?.click(); const image = document.querySelector('#shabaSecurityCodeImg')?.src || ''; return { ready: document.readyState, image, hint: document.querySelector('#shabaSecurityCodecaptchaHint')?.innerText?.trim() || '' }; })()", timeout.Token);
                if (page.GetProperty("ready").GetString() == "complete" && page.GetProperty("image").GetString()?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true) break;
                await Task.Delay(150, timeout.Token);
            }
            var captchaImage = page.GetProperty("image").GetString();
            if (string.IsNullOrWhiteSpace(captchaImage)) throw new InvalidOperationException("تصویر واقعی کد امنیتی بانک تجارت دریافت نشد.");
            var sessionId = Guid.NewGuid().ToString("N");
            sessions[sessionId] = browserSession;
            return new TejaratConversionStart(sessionId, captchaImage, browserSession.ExpiresAtUtc, page.GetProperty("hint").GetString());
        }
        catch { browserSession.Dispose(); throw; }
    }

    public async Task<BankCardConversionResult> CompleteAsync(string owner, string sessionId, string captcha, CancellationToken token)
    {
        if (!sessions.TryRemove(sessionId, out var session) || session.ExpiresAtUtc < DateTimeOffset.UtcNow || !string.Equals(session.Owner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException("نشست تبدیل منقضی شده است؛ تصویر کد امنیتی جدید بگیرید.");
        using (session)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var card = JsonSerializer.Serialize(session.CardNumber);
            var captchaValue = JsonSerializer.Serialize(captcha.Trim());
            await session.DevTools.EvaluateAsync($$"""
                (() => {
                    document.querySelector('#pan_to_iban')?.click();
                    document.querySelector('#inBox').value = {{card}};
                    document.querySelector('#shabaSecurityCode').value = {{captchaValue}};
                    document.querySelector('#senderType').value = '3';
                    const radio = document.querySelector('#panRadioIban'); if (radio) radio.checked = true;
                    setTimeout(() => document.querySelector('#report_builder_form').requestSubmit(document.querySelector('#sendBtn')), 0);
                    return true;
                })()
                """, timeout.Token);
            await Task.Delay(500, timeout.Token);

            JsonElement result = default;
            while (!timeout.IsCancellationRequested)
            {
                result = await session.DevTools.EvaluateAsync("(() => ({ ready: document.readyState, ibanText: document.querySelector('#sct_iban')?.innerText?.trim() || '', name: document.querySelector('#name')?.innerText?.trim() || '', error: document.querySelector('#ctl05_ctl03_ctl00_lbError')?.innerText?.trim() || '' }))()", timeout.Token);
                var resultText = result.GetProperty("ibanText").GetString() ?? string.Empty;
                var errorText = result.GetProperty("error").GetString() ?? string.Empty;
                if (result.GetProperty("ready").GetString() == "complete" && (resultText.Length > 0 || errorText.Length > 0)) break;
                await Task.Delay(150, timeout.Token);
            }

            var ibanText = result.GetProperty("ibanText").GetString() ?? string.Empty;
            var candidates = ibans.Extract(ibanText).Concat(ibans.ExtractCorrectable(ibanText)).Concat(ExtractLooseIban(ibanText)).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 1) return new BankCardConversionResult(candidates[0], EmptyToNull(result.GetProperty("name").GetString()));
            var bankError = EmptyToNull(result.GetProperty("error").GetString()) ?? EmptyToNull(ibanText);
            if (bankError?.Contains("امنیتی", StringComparison.Ordinal) == true) throw new InvalidOperationException("کد امنیتی نادرست یا منقضی است؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
            if (bankError is not null) throw new InvalidOperationException("پیام بانک تجارت: " + bankError);
            throw new InvalidOperationException("بانک تجارت شماره شبا برنگرداند؛ تصویر جدید بگیرید و دوباره تلاش کنید.");
        }
    }

    private static IEnumerable<string> ExtractLooseIban(string text)
    {
        var normalized = IbanService.NormalizeDigits(text);
        var index = normalized.IndexOf("IR", StringComparison.OrdinalIgnoreCase);
        if (index < 0) yield break;
        var digits = new string(normalized.Skip(index + 2).Take(120).Where(char.IsDigit).Take(24).ToArray());
        if (digits.Length == 24) yield return "IR" + digits;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
            var profile = Path.Combine(Path.GetTempPath(), "sms-panel-tejarat-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
            Process? process = null; ClientWebSocket? socket = null;
            try
            {
                var start = new ProcessStartInfo(FindBrowser()) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profile, FormUrl }) start.ArgumentList.Add(argument);
                process = Process.Start(start) ?? throw new InvalidOperationException("مرورگر مخفی بانک تجارت اجرا نشد.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var port = await WaitForPortAsync(profile, process, timeout.Token);
                var uri = await WaitForPageAsync(port, timeout.Token);
                socket = new ClientWebSocket(); await socket.ConnectAsync(uri, timeout.Token);
                return new BrowserSession(owner, cardNumber, process, socket, profile);
            }
            catch { socket?.Dispose(); if (process is { HasExited: false }) process.Kill(true); process?.Dispose(); TryDelete(profile); throw; }
        }

        private static string FindBrowser()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return new[] { Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(x86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(x86, "Microsoft", "Edge", "Application", "msedge.exe") }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("مرورگر کروم یا اج روی سرور نصب نیست.");
        }
        private static async Task<int> WaitForPortAsync(string profile, Process process, CancellationToken token) { var file = Path.Combine(profile, "DevToolsActivePort"); while (true) { token.ThrowIfCancellationRequested(); if (process.HasExited) throw new InvalidOperationException("مرورگر مخفی بانک تجارت بسته شد."); if (File.Exists(file)) { var lines = await File.ReadAllLinesAsync(file, token); if (lines.Length > 0 && int.TryParse(lines[0], out var port)) return port; } await Task.Delay(100, token); } }
        private static async Task<Uri> WaitForPageAsync(int port, CancellationToken token) { using var client = new HttpClient(); while (true) { token.ThrowIfCancellationRequested(); try { using var json = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/json/list", token)); var page = json.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("type").GetString() == "page"); if (page.ValueKind != JsonValueKind.Undefined) return new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!); } catch (HttpRequestException) { } await Task.Delay(100, token); } }
        public void Dispose() { socket.Dispose(); if (!browser.HasExited) { try { browser.Kill(true); browser.WaitForExit(3000); } catch (InvalidOperationException) { } } browser.Dispose(); TryDelete(profilePath); }
        private static void TryDelete(string path) { if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return; try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private sealed class DevToolsClient(ClientWebSocket socket)
    {
        private int id;
        public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
        {
            var response = await SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
            if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException("فرمان مرورگر بانک تجارت اجرا نشد: " + error.GetProperty("message").GetString());
            var outer = response.GetProperty("result");
            if (outer.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("اجرای فرم بانک تجارت در مرورگر ناموفق بود.");
            return outer.GetProperty("result").GetProperty("value").Clone();
        }
        private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token)
        {
            var commandId = Interlocked.Increment(ref id); var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = commandId, method, @params = parameters }); await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); var buffer = new byte[16384];
            while (true) { using var stream = new MemoryStream(); WebSocketReceiveResult read; do { read = await socket.ReceiveAsync(buffer, token); if (read.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("ارتباط مرورگر بانک تجارت بسته شد."); stream.Write(buffer, 0, read.Count); } while (!read.EndOfMessage); using var json = JsonDocument.Parse(stream.ToArray()); if (json.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == commandId) return json.RootElement.Clone(); }
        }
    }
}

public sealed record TejaratConversionStart(string SessionId, string CaptchaImage, DateTimeOffset ExpiresAtUtc, string? CaptchaHint);
