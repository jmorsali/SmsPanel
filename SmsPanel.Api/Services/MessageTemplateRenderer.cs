using System.Text.RegularExpressions;
using SmsPanel.Api.Domain;

namespace SmsPanel.Api.Services;

public sealed partial class MessageTemplateRenderer
{
    private static readonly IReadOnlyDictionary<string, Func<Person, string?, string>> Accessors =
        new Dictionary<string, Func<Person, string?, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = (person, _) => person.FirstName,
            ["LastName"] = (person, _) => person.LastName,
            ["FullName"] = (person, _) => FullName(person),
            ["NationalCode"] = (person, _) => person.NationalCode,
            ["Mobile"] = (person, _) => person.Mobile,
            ["CurrentIban"] = (person, _) => person.CurrentIban ?? string.Empty,
            ["CurrentBank"] = (_, currentBank) => currentBank ?? string.Empty,
            ["نام"] = (person, _) => person.FirstName,
            ["نام_خانوادگی"] = (person, _) => person.LastName,
            ["نام_کامل"] = (person, _) => FullName(person),
            ["کد_ملی"] = (person, _) => person.NationalCode,
            ["شماره_موبایل"] = (person, _) => person.Mobile,
            ["شبای_فعلی"] = (person, _) => person.CurrentIban ?? string.Empty,
            ["بانک_فعلی"] = (_, currentBank) => currentBank ?? string.Empty
        };

    public IReadOnlyList<ResponseTemplateField> Fields { get; } =
    [
        new("FirstName", "{{نام}}", "نام مخاطب", "علی", false),
        new("LastName", "{{نام_خانوادگی}}", "نام خانوادگی مخاطب", "احمدی", false),
        new("FullName", "{{نام_کامل}}", "نام و نام خانوادگی پذیرنده", "علی احمدی", false),
        new("CurrentBank", "{{بانک_فعلی}}", "بانک حساب فعلی", "پارسیان", false),
        new("NationalCode", "{{کد_ملی}}", "کد ملی مخاطب", "۰۰۱۲۳۴۵۶۷۸", true),
        new("Mobile", "{{شماره_موبایل}}", "شماره موبایل مخاطب", "۰۹۱۲۱۲۳۴۵۶۷", true),
        new("CurrentIban", "{{شبای_فعلی}}", "شبای فعلی مخاطب", "IR820540102680020817909002", true)
    ];

    public IReadOnlyList<ResponseTemplateField> ConfirmationFields =>
    [
        .. Fields,
        new("ReceivedIbanLast4", "{{چهار_رقم_آخر_شبای_دریافتی}}", "چهار رقم آخر شبای دریافتی", "۹۰۰۲", true)
    ];

    public IReadOnlyList<ResponseTemplateField> InvalidReplyFields =>
    [
        .. Fields,
        new("InvalidReason", "{{دلیل_نامعتبر_بودن}}", "دلیل نامعتبر بودن پاسخ", "شماره شبای معتبر در پیام پیدا نشد", false)
    ];

    public IReadOnlyList<string> GetUnknownFields(string template) => Placeholder().Matches(template)
        .Select(match => match.Groups[1].Value)
        .Where(field => !Accessors.ContainsKey(field))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string Render(string template, Person person, string? currentBank = null) => Placeholder().Replace(template, match =>
    {
        var field = match.Groups[1].Value;
        return Accessors.TryGetValue(field, out var accessor) ? accessor(person, currentBank) : match.Value;
    });

    public IReadOnlyList<string> GetUnknownConfirmationFields(string template) => Placeholder().Matches(template)
        .Select(match => match.Groups[1].Value)
        .Where(field => !Accessors.ContainsKey(field) && !field.Equals("چهار_رقم_آخر_شبای_دریافتی", StringComparison.OrdinalIgnoreCase) && !field.Equals("ReceivedIbanLast4", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string RenderConfirmation(string template, Person person, string receivedIban) => Placeholder().Replace(template, match =>
    {
        var field = match.Groups[1].Value;
        if (field.Equals("چهار_رقم_آخر_شبای_دریافتی", StringComparison.OrdinalIgnoreCase) || field.Equals("ReceivedIbanLast4", StringComparison.OrdinalIgnoreCase)) return receivedIban[^4..];
        return Accessors.TryGetValue(field, out var accessor) ? accessor(person, null) : match.Value;
    });

    public IReadOnlyList<string> GetUnknownInvalidReplyFields(string template) => Placeholder().Matches(template)
        .Select(match => match.Groups[1].Value)
        .Where(field => !Accessors.ContainsKey(field) && !field.Equals("دلیل_نامعتبر_بودن", StringComparison.OrdinalIgnoreCase) && !field.Equals("InvalidReason", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string RenderInvalidReply(string template, Person person, string reason) => Placeholder().Replace(template, match =>
    {
        var field = match.Groups[1].Value;
        if (field.Equals("دلیل_نامعتبر_بودن", StringComparison.OrdinalIgnoreCase) || field.Equals("InvalidReason", StringComparison.OrdinalIgnoreCase)) return reason;
        return Accessors.TryGetValue(field, out var accessor) ? accessor(person, null) : match.Value;
    });

    private static string FullName(Person person) => $"{person.FirstName} {person.LastName}".Trim();

    [GeneratedRegex(@"\{\{\s*([\p{L}][\p{L}\p{N}_]*)\s*\}\}")]
    private static partial Regex Placeholder();
}

public sealed record ResponseTemplateField(string Name, string Token, string Label, string Sample, bool Sensitive);
