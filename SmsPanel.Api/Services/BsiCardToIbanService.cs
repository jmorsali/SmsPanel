using System.Diagnostics;
using System.Net.WebSockets;
using System.Numerics;
using System.Text.Json;

namespace SmsPanel.Api.Services;

public sealed class BsiCardToIbanService(IbanService ibans) : IDisposable
{
    private const string FormUrl = "https://bsi.ir/ibanc/index.aspx";
    private const string ApiUrl = "https://apps.bsi.ir/api-iban/card";
    private readonly SemaphoreSlim browserGate = new(2, 2);

    public async Task<BankCardConversionResult> ConvertAsync(string cardNumber, CancellationToken token)
    {
        if (!cardNumber.StartsWith("603769", StringComparison.Ordinal)) throw new InvalidOperationException("این سرویس فقط کارت‌های بانک صادرات را پشتیبانی می‌کند.");
        await browserGate.WaitAsync(token);
        try { return await ConvertInBrowserAsync(cardNumber, token); }
        finally { browserGate.Release(); }
    }

    public async Task<string?> LookupIbanOwnerAsync(string iban, CancellationToken token)
    {
        var normalized = "IR" + new string(iban.Skip(2).Where(char.IsDigit).ToArray());
        if (!ibans.IsValid(normalized) || normalized.Substring(4, 3) != "019") throw new InvalidOperationException("این استعلام فقط شبای بانک صادرات را پشتیبانی می‌کند.");
        var accountNumber = normalized[14..];
        await browserGate.WaitAsync(token);
        try { return await LookupAccountOwnerInBrowserAsync(accountNumber, token); }
        finally { browserGate.Release(); }
    }

    private async Task<string?> LookupAccountOwnerInBrowserAsync(string accountNumber, CancellationToken token)
    {
        var profilePath = Path.Combine(Path.GetTempPath(), "sms-panel-bsi-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);
        Process? browser = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var start = new ProcessStartInfo(FindBrowser()) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profilePath, FormUrl }) start.ArgumentList.Add(argument);
            browser = Process.Start(start) ?? throw new InvalidOperationException("مرورگر مخفی برای استعلام مالک شبا اجرا نشد.");
            var port = await WaitForDebuggingPortAsync(profilePath, browser, timeout.Token);
            var pageSocket = await WaitForPageSocketAsync(port, timeout.Token);
            using var socket = new ClientWebSocket(); await socket.ConnectAsync(pageSocket, timeout.Token);
            var cdp = new ChromeDevToolsClient(socket); await cdp.WaitForDocumentAsync(timeout.Token);
            var expression = $$"""
                (async () => {
                    const response = await fetch({{JsonSerializer.Serialize(ApiUrl)}}, {
                        method: 'POST', headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ 'accountNumber.equals': {{JsonSerializer.Serialize(accountNumber)}} })
                    });
                    return { status: response.status, contentType: response.headers.get('content-type') || '', body: await response.text() };
                })()
                """;
            var result = await cdp.EvaluateAsync(expression, timeout.Token);
            if (result.GetProperty("status").GetInt32() is < 200 or >= 300 || !(result.GetProperty("contentType").GetString() ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("بانک صادرات پاسخ معتبر مالک حساب برنگرداند.");
            using var document = JsonDocument.Parse(result.GetProperty("body").GetString() ?? string.Empty);
            return document.RootElement.TryGetProperty("customerInfo", out var customer) ? ExtractCustomerName(customer) : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("مهلت استعلام مالک شبا از بانک صادرات به پایان رسید."); }
        finally { if (browser is { HasExited: false }) { try { browser.Kill(true); } catch (InvalidOperationException) { } } browser?.Dispose(); TryDeleteProfile(profilePath); }
    }

    private async Task<BankCardConversionResult> ConvertInBrowserAsync(string cardNumber, CancellationToken token)
    {
        var profilePath = Path.Combine(Path.GetTempPath(), "sms-panel-bsi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);
        Process? browser = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var start = new ProcessStartInfo(FindBrowser()) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profilePath, FormUrl }) start.ArgumentList.Add(argument);
            browser = Process.Start(start) ?? throw new InvalidOperationException("مرورگر مخفی برای استعلام بانک صادرات اجرا نشد.");
            var port = await WaitForDebuggingPortAsync(profilePath, browser, timeout.Token);
            var pageSocket = await WaitForPageSocketAsync(port, timeout.Token);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(pageSocket, timeout.Token);
            var cdp = new ChromeDevToolsClient(socket);
            await cdp.WaitForDocumentAsync(timeout.Token);

            var expression = $$"""
                (async () => {
                    const response = await fetch({{JsonSerializer.Serialize(ApiUrl)}}, {
                        method: 'POST', headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ 'cardNumber.equals': {{JsonSerializer.Serialize(cardNumber)}} })
                    });
                    const body = await response.text();
                    let ownerBody = '';
                    if (response.ok) {
                        const card = JSON.parse(body);
                        const accountNumber = card?.cardInfo?.accountNumber;
                        if (accountNumber) {
                            const ownerResponse = await fetch({{JsonSerializer.Serialize(ApiUrl)}}, {
                                method: 'POST', headers: { 'Content-Type': 'application/json' },
                                body: JSON.stringify({ 'accountNumber.equals': accountNumber })
                            });
                            if (ownerResponse.ok) ownerBody = await ownerResponse.text();
                        }
                    }
                    return { status: response.status, contentType: response.headers.get('content-type') || '', body, ownerBody };
                })()
                """;
            var browserResult = await cdp.EvaluateAsync(expression, timeout.Token);
            var status = browserResult.GetProperty("status").GetInt32();
            var contentType = browserResult.GetProperty("contentType").GetString() ?? string.Empty;
            var body = browserResult.GetProperty("body").GetString() ?? string.Empty;
            if (status is < 200 or >= 300) throw new InvalidOperationException($"بانک صادرات درخواست مرورگر را نپذیرفت ({status}).");
            if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("بانک صادرات به‌جای اطلاعات کارت، پاسخ امنیتی برگرداند.");
            return ParseResult(body, browserResult.GetProperty("ownerBody").GetString());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("مهلت استعلام از صفحه رسمی بانک صادرات به پایان رسید."); }
        finally
        {
            if (browser is { HasExited: false }) { try { browser.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            browser?.Dispose();
            TryDeleteProfile(profilePath);
        }
    }

    private BankCardConversionResult ParseResult(string json, string? ownerJson)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("cardInfo", out var cardInfo) || !cardInfo.TryGetProperty("accountNumber", out var accountElement)) throw new InvalidOperationException("بانک صادرات برای این کارت اطلاعات حساب برنگرداند.");
            var accountNumber = accountElement.ToString().Trim();
            if (accountNumber.Length == 0 || accountNumber.Any(value => !char.IsDigit(value))) throw new InvalidOperationException("شماره حساب برگشتی بانک صادرات معتبر نیست.");
            // The official BSI page prefixes the API's card account number with zero
            // before constructing the card-to-IBAN BBAN (generateIBANC).
            var bban = "0190000000" + accountNumber;
            if (bban.Length != 22) throw new InvalidOperationException("ساختار شماره حساب برگشتی بانک صادرات قابل تبدیل نیست.");
            var remainder = BigInteger.Zero;
            foreach (var digit in bban + "182700") remainder = (remainder * 10 + digit - '0') % 97;
            var iban = $"IR{98 - (int)remainder:00}{bban}";
            if (!ibans.IsValid(iban)) throw new InvalidOperationException("شماره شبای محاسبه‌شده از پاسخ بانک صادرات معتبر نیست.");
            return new BankCardConversionResult(iban, ExtractOwnerName(cardInfo, ownerJson));
        }
        catch (JsonException) { throw new InvalidOperationException("پاسخ بانک صادرات قابل خواندن نیست."); }
    }

    private static string? ExtractOwnerName(JsonElement cardInfo, string? ownerJson)
    {
        if (!string.IsNullOrWhiteSpace(ownerJson))
        {
            try
            {
                using var ownerDocument = JsonDocument.Parse(ownerJson);
                if (ownerDocument.RootElement.TryGetProperty("customerInfo", out var customer))
                {
                    var fullName = ExtractCustomerName(customer);
                    if (fullName.Length > 0) return fullName;
                }
            }
            catch (JsonException) { }
        }
        var cardFirstName = cardInfo.TryGetProperty("firstName", out var firstName) ? firstName.ToString() : string.Empty;
        var cardLastName = cardInfo.TryGetProperty("lastName", out var lastName) ? lastName.ToString() : string.Empty;
        var cardFullName = $"{cardFirstName} {cardLastName}".Trim();
        return cardFullName.Length == 0 ? null : cardFullName;
    }

    private static string ExtractCustomerName(JsonElement customer)
    {
        var name = customer.TryGetProperty("name", out var first) ? first.ToString() : string.Empty;
        var lastName = customer.TryGetProperty("lastName", out var last) ? last.ToString() : string.Empty;
        return $"{name} {lastName}".Trim();
    }

    private static string FindBrowser()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[] { Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(pfx86, "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe") };
        return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("برای استعلام بانک صادرات، نصب بودن مرورگر کروم یا اج روی سرور لازم است.");
    }

    private static async Task<int> WaitForDebuggingPortAsync(string profilePath, Process browser, CancellationToken token)
    {
        var file = Path.Combine(profilePath, "DevToolsActivePort");
        while (!token.IsCancellationRequested)
        {
            if (browser.HasExited) throw new InvalidOperationException("مرورگر مخفی بانک صادرات پیش از آماده‌شدن بسته شد.");
            if (File.Exists(file)) { var lines = await File.ReadAllLinesAsync(file, token); if (lines.Length > 0 && int.TryParse(lines[0], out var port)) return port; }
            await Task.Delay(100, token);
        }
        throw new OperationCanceledException(token);
    }

    private static async Task<Uri> WaitForPageSocketAsync(int port, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (!token.IsCancellationRequested)
        {
            try
            {
                var json = await client.GetStringAsync($"http://127.0.0.1:{port}/json/list", token);
                using var document = JsonDocument.Parse(json);
                var page = document.RootElement.EnumerateArray().FirstOrDefault(item => item.GetProperty("type").GetString() == "page");
                if (page.ValueKind != JsonValueKind.Undefined && page.TryGetProperty("webSocketDebuggerUrl", out var url)) return new Uri(url.GetString()!);
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, token);
        }
        throw new OperationCanceledException(token);
    }

    private static void TryDeleteProfile(string profilePath)
    {
        if (!Path.GetFullPath(profilePath).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (Directory.Exists(profilePath)) Directory.Delete(profilePath, recursive: true); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    public void Dispose() => browserGate.Dispose();

    private sealed class ChromeDevToolsClient(ClientWebSocket socket)
    {
        private int commandId;
        public async Task WaitForDocumentAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var result = await EvaluateAsync("({ state: document.readyState, url: location.href })", token);
                if (result.GetProperty("state").GetString() == "complete" && result.GetProperty("url").GetString()?.StartsWith("https://bsi.ir/ibanc/", StringComparison.OrdinalIgnoreCase) == true) return;
                await Task.Delay(150, token);
            }
            throw new OperationCanceledException(token);
        }

        public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
        {
            var response = await SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
            if (response.TryGetProperty("error", out var protocolError)) throw new InvalidOperationException("مرورگر مخفی بانک صادرات فرمان استعلام را نپذیرفت: " + protocolError.GetProperty("message").GetString());
            var result = response.GetProperty("result").GetProperty("result");
            if (result.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("اجرای استعلام در صفحه رسمی بانک صادرات ناموفق بود.");
            if (!result.TryGetProperty("value", out var value)) throw new InvalidOperationException("صفحه رسمی بانک صادرات نتیجه‌ای برنگرداند.");
            return value.Clone();
        }

        private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref commandId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("ارتباط با مرورگر مخفی بانک صادرات بسته شد.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id) return document.RootElement.Clone();
            }
        }
    }
}
