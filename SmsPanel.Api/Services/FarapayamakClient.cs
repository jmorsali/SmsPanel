using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
namespace SmsPanel.Api.Services;

public sealed class FarapayamakOptions { public string BaseUrl { get; set; } = "https://rest.payamak-panel.com/api/SendSMS/"; public string Username { get; set; } = ""; public string Password { get; set; } = ""; public string FromNumber { get; set; } = ""; public bool PollingEnabled { get; set; } = true; public int PollingIntervalMinutes { get; set; } = 1; public int InboxPageSize { get; set; } = 100; public int SendBatchSize { get; set; } = 100; public int SendConcurrency { get; set; } = 5; }
public sealed record ProviderResponse(string Value, int RetStatus, string? StrRetStatus) { public bool IsSuccess => RetStatus == 1; }
public sealed record ProviderInboundMessage(string Id, string Sender, string? Receiver, string Text, DateTimeOffset ReceivedAtUtc);
public interface IFarapayamakClient { Task<ProviderResponse> SendAsync(IReadOnlyCollection<string> recipients, string text, CancellationToken token); Task<IReadOnlyList<ProviderInboundMessage>> GetMessagesAsync(int index, int count, CancellationToken token); }
public sealed class FarapayamakClient(HttpClient http, IOptions<FarapayamakOptions> settings, ILogger<FarapayamakClient> log, InboundTextDecoder textDecoder) : IFarapayamakClient
{
    private readonly FarapayamakOptions o = settings.Value;
    public async Task<ProviderResponse> SendAsync(IReadOnlyCollection<string> recipients, string text, CancellationToken token)
    {
        var envelope = await Post("SendSMS", new() { ["username"] = o.Username, ["password"] = o.Password, ["from"] = o.FromNumber, ["to"] = string.Join(",", recipients), ["text"] = text, ["isFlash"] = false }, token, formEncoded: true);
        return envelope.Response;
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
