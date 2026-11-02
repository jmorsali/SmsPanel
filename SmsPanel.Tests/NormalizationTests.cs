using SmsPanel.Api.Services;
namespace SmsPanel.Tests;

public sealed class NormalizationTests
{
    [Theory]
    [InlineData("+98 912 123 4567", "09121234567")]
    [InlineData("۰۹۱۲۱۲۳۴۵۶۷", "09121234567")]
    public void Mobile_is_normalized(string input, string expected) => Assert.Equal(expected, new MobileNormalizer().Normalize(input));
    [Fact] public void Persian_digits_are_extracted_and_validated() { var s = new IbanService(); var values = s.Extract("شبا IR۸۲۰۵۴۰۱۰۲۶۸۰۰۲۰۸۱۷۹۰۹۰۰۲"); Assert.Single(values); Assert.True(s.IsValid(values[0])); }
    [Fact] public void Invalid_checksum_is_rejected() => Assert.False(new IbanService().IsValid("IR0001700000001003242001"));
}
