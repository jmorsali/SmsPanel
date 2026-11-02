using System.Text.RegularExpressions;
using SmsPanel.Api.Domain;

namespace SmsPanel.Api.Services;

public sealed partial class MessageTemplateRenderer
{
    private static readonly IReadOnlyDictionary<string, Func<Person, string>> Accessors =
        new Dictionary<string, Func<Person, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = person => person.FirstName,
            ["LastName"] = person => person.LastName,
            ["NationalCode"] = person => person.NationalCode,
            ["Mobile"] = person => person.Mobile,
            ["CurrentIban"] = person => person.CurrentIban ?? string.Empty
            , ["نام"] = person => person.FirstName
            , ["نام_خانوادگی"] = person => person.LastName
            , ["کد_ملی"] = person => person.NationalCode
            , ["شماره_موبایل"] = person => person.Mobile
            , ["شبای_فعلی"] = person => person.CurrentIban ?? string.Empty
        };

    public IReadOnlyList<ResponseTemplateField> Fields { get; } =
    [
        new("FirstName", "{{نام}}", "نام مخاطب", "علی", false),
        new("LastName", "{{نام_خانوادگی}}", "نام خانوادگی مخاطب", "احمدی", false),
        new("NationalCode", "{{کد_ملی}}", "کد ملی مخاطب", "۰۰۱۲۳۴۵۶۷۸", true),
        new("Mobile", "{{شماره_موبایل}}", "شماره موبایل مخاطب", "۰۹۱۲۱۲۳۴۵۶۷", true),
        new("CurrentIban", "{{شبای_فعلی}}", "شبای فعلی مخاطب", "IR820540102680020817909002", true)
    ];

    public IReadOnlyList<ResponseTemplateField> ConfirmationFields =>
    [
        .. Fields,
        new("ReceivedIbanLast4", "{{چهار_رقم_آخر_شبای_دریافتی}}", "چهار رقم آخر شبای دریافتی", "۹۰۰۲", true)
    ];

    public IReadOnlyList<string> GetUnknownFields(string template) => Placeholder().Matches(template)
        .Select(match => match.Groups[1].Value)
        .Where(field => !Accessors.ContainsKey(field))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string Render(string template, Person person) => Placeholder().Replace(template, match =>
    {
        var field = match.Groups[1].Value;
        return Accessors.TryGetValue(field, out var accessor) ? accessor(person) : match.Value;
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
        return Accessors.TryGetValue(field, out var accessor) ? accessor(person) : match.Value;
    });

    [GeneratedRegex(@"\{\{\s*([\p{L}][\p{L}\p{N}_]*)\s*\}\}")]
    private static partial Regex Placeholder();
}

public sealed record ResponseTemplateField(string Name, string Token, string Label, string Sample, bool Sensitive);
