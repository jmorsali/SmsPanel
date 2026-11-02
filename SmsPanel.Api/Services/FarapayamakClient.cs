using System.Text.Json;
using Microsoft.Extensions.Options;
namespace SmsPanel.Api.Services;

public sealed class FarapayamakOptions { public string BaseUrl { get; set; } = "https://rest.payamak-panel.com/api/SendSMS/"; public string Username { get; set; } = ""; public string Password { get; set; } = ""; public string FromNumber { get; set; } = ""; public bool PollingEnabled { get; set; } = true; public int PollingIntervalMinutes { get; set; } = 5; public int InboxPageSize { get; set; } = 100; public int SendBatchSize { get; set; } = 100; }
public sealed record ProviderResponse(string Value, int RetStatus, string? StrRetStatus) { public bool IsSuccess => RetStatus == 1; }
public sealed record ProviderInboundMessage(string Id, string Sender, string? Receiver, string Text, DateTimeOffset ReceivedAtUtc);
public interface IFarapayamakClient { Task<ProviderResponse> SendAsync(IReadOnlyCollection<string> recipients, string text, CancellationToken token); Task<IReadOnlyList<ProviderInboundMessage>> GetMessagesAsync(int index, int count, CancellationToken token); }
public sealed class FarapayamakClient(HttpClient http, IOptions<FarapayamakOptions> settings) : IFarapayamakClient
{
    private readonly FarapayamakOptions o = settings.Value;
    public Task<ProviderResponse> SendAsync(IReadOnlyCollection<string> recipients, string text, CancellationToken token) => Post("SendSMS", new() { ["username"] = o.Username, ["password"] = o.Password, ["from"] = o.FromNumber, ["to"] = string.Join(",", recipients), ["text"] = text, ["isFlash"] = "false" }, token);
    public async Task<IReadOnlyList<ProviderInboundMessage>> GetMessagesAsync(int index, int count, CancellationToken token)
    {
        var r = await Post("GetMessages", new() { ["UserName"] = o.Username, ["PassWord"] = o.Password, ["Location"] = "1", ["From"] = o.FromNumber, ["Index"] = index.ToString(), ["Count"] = count.ToString() }, token);
        if (!r.IsSuccess || string.IsNullOrWhiteSpace(r.Value)) return [];
        using var doc = JsonDocument.Parse(r.Value); if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray().Select(x => new ProviderInboundMessage(Read(x, "MessageId", "Id", "MsgId"), Read(x, "Sender", "From", "Mobile"), ReadOptional(x, "Receiver", "To"), Read(x, "Body", "Text", "Message"), DateTimeOffset.TryParse(ReadOptional(x, "ReceiveDate", "Date", "ReceivedAt"), out var d) ? d : DateTimeOffset.UtcNow)).ToList();
    }
    private async Task<ProviderResponse> Post(string op, Dictionary<string, string> fields, CancellationToken token) { using var response = await http.PostAsync(new Uri(new Uri(o.BaseUrl), op), new FormUrlEncodedContent(fields), token); var body = await response.Content.ReadAsStringAsync(token); response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<ProviderResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("پاسخ فراپیامک قابل خواندن نیست."); }
    private static string Read(JsonElement item, params string[] names) => ReadOptional(item, names) ?? throw new JsonException($"فیلد {string.Join('/', names)} یافت نشد.");
    private static string? ReadOptional(JsonElement item, params string[] names) { foreach (var p in item.EnumerateObject()) if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return p.Value.ToString(); return null; }
}
