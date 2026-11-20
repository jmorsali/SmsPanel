using System.Globalization;
using System.Text.Json;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
namespace SmsPanel.Api.Services;

public sealed class FarapayamakOptions { public string BaseUrl { get; set; } = "https://rest.payamak-panel.com/api/SendSMS/"; public string Username { get; set; } = ""; public string Password { get; set; } = ""; public string FromNumber { get; set; } = ""; public bool PollingEnabled { get; set; } = true; public int PollingIntervalMinutes { get; set; } = 1; public int InboxPageSize { get; set; } = 100; public int SendBatchSize { get; set; } = 100; public int SendConcurrency { get; set; } = 5; }
public sealed record ProviderResponse(string Value, int RetStatus, string? StrRetStatus)
{
    public bool IsSuccess => RetStatus == 1;
    public bool IsInsufficientCredit => !IsSuccess && Value.Trim() == "2";
    public string ErrorMessage => IsInsufficientCredit
        ? "اعتبار پنل پیامک کافی نیست؛ ارسال متوقف شد. پس از افزایش اعتبار، ادامه ارسال را بزنید."
        : Value.Trim() == "7"
            ? "متن پیام توسط فیلتر سرویس رد شد."
            : StrRetStatus ?? $"خطای سرویس پیامک ({Value})";
}
public sealed record ProviderInboundMessage(string Id, string Sender, string? Receiver, string Text, DateTimeOffset ReceivedAtUtc);
public sealed record ProviderCreditTransaction(long Id, decimal Amount, decimal RemainingCredit, DateTimeOffset TransactedAtUtc, string? Description, int Type, long SendCode);
public interface IFarapayamakClient
{
    Task<ProviderResponse> SendAsync(IReadOnlyCollection<string> recipients, string text, CancellationToken token);
    Task<IReadOnlyList<ProviderInboundMessage>> GetMessagesAsync(int index, int count, CancellationToken token);
    Task<decimal> GetCreditAsync(CancellationToken token);
    Task<IReadOnlyList<ProviderCreditTransaction>> GetCreditTransactionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken token);
    Task<ProviderCreditTransaction?> GetLatestCreditTransactionAsync(CancellationToken token);
}
public sealed class FarapayamakClient(HttpClient http, IOptions<FarapayamakOptions> settings, ILogger<FarapayamakClient> log, InboundTextDecoder textDecoder) : IFarapayamakClient
{
    private const int CreditTransactionBatchSize = 100;
    public const int CreditTransactionLimit = 10_000;
    private readonly FarapayamakOptions o = settings.Value;
    public async Task<ProviderResponse> SendAsync(IReadOnlyCollection<string> recipients, string text, CancellationToken token)
    {
        var envelope = await Post("SendSMS", new() { ["username"] = o.Username, ["password"] = o.Password, ["from"] = o.FromNumber, ["to"] = string.Join(",", recipients), ["text"] = text, ["isFlash"] = false }, token, formEncoded: true);
        return envelope.Response;
    }
    public async Task<decimal> GetCreditAsync(CancellationToken token)
    {
        var envelope = await Post("GetCredit", new() { ["UserName"] = o.Username, ["PassWord"] = o.Password }, token, formEncoded: true);
        if (!envelope.Response.IsSuccess) throw new InvalidOperationException(envelope.Response.ErrorMessage);
        if (!decimal.TryParse(envelope.Response.Value, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out var credit))
            throw new InvalidOperationException("موجودی برگشتی فراپیامک قابل خواندن نیست.");
        return credit;
    }
    public async Task<IReadOnlyList<ProviderCreditTransaction>> GetCreditTransactionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken token)
    {
        const int concurrentPages = 4;
        var result = await GetCreditTransactionPageAsync(fromUtc, toUtc, 0, CreditTransactionBatchSize, token);
        if (result.Count < CreditTransactionBatchSize) return result;

        for (var index = CreditTransactionBatchSize; index < CreditTransactionLimit; index += CreditTransactionBatchSize * concurrentPages)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pageCount = Math.Min(concurrentPages, (CreditTransactionLimit - index) / CreditTransactionBatchSize);
            var pages = Enumerable.Range(0, pageCount)
                .Select(page => GetCreditTransactionPageAsync(fromUtc, toUtc, index + page * CreditTransactionBatchSize, CreditTransactionBatchSize, pending.Token))
                .ToArray();
            try
            {
                foreach (var page in pages)
                {
                    var rows = await page;
                    result.AddRange(rows);
                    if (rows.Count < CreditTransactionBatchSize) return result;
                }
            }
            finally
            {
                pending.Cancel();
                await ((Task)Task.WhenAll(pages)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        return result;
    }

    public async Task<ProviderCreditTransaction?> GetLatestCreditTransactionAsync(CancellationToken token)
    {
        var rows = await GetCreditTransactionPageAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, 0, 1, token);
        return rows.FirstOrDefault();
    }

    private async Task<List<ProviderCreditTransaction>> GetCreditTransactionPageAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int index, int count, CancellationToken token)
    {
        var serviceNamespace = XName.Get("GetUserTransactions2", "http://tempuri.org/").Namespace;
        var soap = XNamespace.Get("http://schemas.xmlsoap.org/soap/envelope/");
        var envelope = new XDocument(
            new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(serviceNamespace + "GetUserTransactions2",
                    new XElement(serviceNamespace + "username", o.Username), new XElement(serviceNamespace + "password", o.Password), new XElement(serviceNamespace + "targetUsername", o.Username),
                    new XElement(serviceNamespace + "creditType", "All"), new XElement(serviceNamespace + "dateFrom", fromUtc.UtcDateTime.ToString("O")), new XElement(serviceNamespace + "dateTo", toUtc.UtcDateTime.ToString("O")),
                    new XElement(serviceNamespace + "count", count), new XElement(serviceNamespace + "index", index)))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.payamak-panel.com/post/users.asmx");
        request.Headers.Add("SOAPAction", "\"http://tempuri.org/GetUserTransactions2\"");
        request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), System.Text.Encoding.UTF8, "text/xml");
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var operation = document.Root?.Element(soap + "Body")?.Element(serviceNamespace + "GetUserTransactions2Response");
        if (operation is null) throw new InvalidDataException("پاسخ گزارش فراپیامک ساختار مورد انتظار را ندارد.");
        return operation.Descendants(serviceNamespace + "CreditList").Select(ParseCreditTransaction).ToList();
    }

    private static ProviderCreditTransaction ParseCreditTransaction(XElement row)
    {
        string Value(string name) => row.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? string.Empty;
        return new ProviderCreditTransaction(
            long.Parse(Value("CreditID"), System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(Value("CreditAmount"), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture),
            decimal.Parse(Value("CreditRemain"), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Value("TransactDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal).ToUniversalTime(),
            string.IsNullOrWhiteSpace(Value("Description")) ? null : Value("Description").Trim(),
            int.Parse(Value("CreditType"), System.Globalization.CultureInfo.InvariantCulture),
            long.TryParse(Value("SendCode"), out var sendCode) ? sendCode : 0);
    }
    public async Task<IReadOnlyList<ProviderInboundMessage>> GetMessagesAsync(int index, int count, CancellationToken token)
    {
        var envelope = await Post("GetMessages", new() { ["username"] = o.Username, ["password"] = o.Password, ["location"] = 1, ["from"] = o.FromNumber, ["index"] = index, ["count"] = count }, token);
        if (!envelope.Response.IsSuccess) { log.LogWarning("واکشی پیام‌ها توسط سرویس رد شد. RetStatus={RetStatus}, Status={Status}", envelope.Response.RetStatus, envelope.Response.StrRetStatus); return []; }
        JsonElement data;
        if (envelope.Data is { ValueKind: JsonValueKind.Array } currentData) data = currentData;
        else if (!string.IsNullOrWhiteSpace(envelope.Response.Value)) { using var legacy = JsonDocument.Parse(envelope.Response.Value); data = legacy.RootElement.Clone(); }
        else return [];
        if (data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(x => new ProviderInboundMessage(Read(x, "MsgID", "MessageId", "Id", "MsgId"), Read(x, "Sender", "From", "Mobile"), ReadOptional(x, "Receiver", "To"), textDecoder.Decode(Read(x, "Body", "Text", "Message")), DateTimeOffset.TryParse(ReadOptional(x, "SendDate", "ReceiveDate", "Date", "ReceivedAt"), out var d) ? d : DateTimeOffset.UtcNow)).ToList();
    }
    private async Task<ProviderEnvelope> Post(string op, Dictionary<string, object> fields, CancellationToken token, bool formEncoded = false)
    {
        using HttpContent content = formEncoded
            ? new FormUrlEncodedContent(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value is bool flag ? flag.ToString() : Convert.ToString(x.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)))
            : JsonContent.Create(fields);
        using var response = await http.PostAsync(new Uri(new Uri(o.BaseUrl), op), content, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("فراپیامک برای عملیات {Operation} خطای HTTP {StatusCode} برگرداند.", op, (int)response.StatusCode);
            throw new HttpRequestException($"سرویس فراپیامک برای عملیات {op} خطای HTTP {(int)response.StatusCode} برگرداند.");
        }
        using var document = JsonDocument.Parse(body); var root = document.RootElement;
        if (TryProperty(root, "MyBase", out var baseElement))
        {
            var result = baseElement.Deserialize<ProviderResponse>(JsonOptions) ?? throw new InvalidOperationException("پاسخ پایه فراپیامک قابل خواندن نیست.");
            JsonElement? data = TryProperty(root, "Data", out var dataElement) ? dataElement.Clone() : null;
            var reference = data is { ValueKind: JsonValueKind.String or JsonValueKind.Number } scalar ? scalar.ToString() : result.Value;
            return new ProviderEnvelope(result with { Value = reference }, data);
        }
        var legacy = root.Deserialize<ProviderResponse>(JsonOptions) ?? throw new InvalidOperationException("پاسخ فراپیامک قابل خواندن نیست.");
        return new ProviderEnvelope(legacy, null);
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool TryProperty(JsonElement element, string name, out JsonElement value) { foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } value = default; return false; }
    private static string Read(JsonElement item, params string[] names) => ReadOptional(item, names) ?? throw new JsonException($"فیلد {string.Join('/', names)} یافت نشد.");
    private static string? ReadOptional(JsonElement item, params string[] names) { foreach (var p in item.EnumerateObject()) if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return p.Value.ToString(); return null; }
}
public sealed record ProviderEnvelope(ProviderResponse Response, JsonElement? Data);
