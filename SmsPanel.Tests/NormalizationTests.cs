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

    [Theory]
    [InlineData("سلام 5001-3010-0000-0002-11451575 بانک رفاه", "IR500130100000000211451575")]
    [InlineData("شماره شبا: 380540/124020/101149/568601", "IR380540124020101149568601")]
    public void Grouped_iban_without_country_prefix_is_extracted(string input, string expected)
    {
        var service = new IbanService();
        var candidate = Assert.Single(service.Extract(input).Concat(service.ExtractCorrectable(input)).Distinct());
        Assert.Equal(expected, candidate);
        Assert.True(service.IsValid(candidate));
    }

    [Fact]
    public void One_extra_digit_is_removed_only_when_the_valid_result_is_unique()
    {
        var candidate = Assert.Single(new IbanService().ExtractCorrectable("2910180000000279006498104"));
        Assert.Equal("IR290180000000279006498104", candidate);
    }

    [Fact]
    public void One_missing_digit_is_inserted_only_when_the_valid_result_is_unique()
    {
        var service = new IbanService();
        Assert.Equal("IR380540124020101149568601", Assert.Single(service.ExtractCorrectable("38040124020101149568601")));
        Assert.Empty(service.ExtractCorrectable("80540124020101149568601"));
    }

    [Theory]
    [InlineData("علی احمدی", "علی", "احمدی")]
    [InlineData("محمد رضا دارستانی فراهانی", "محمد رضا", "دارستانی فراهانی")]
    [InlineData("سرکار خانم سیده زهرا موسوی", "سیده زهرا", "موسوی")]
    public void Persian_full_name_is_split_conservatively(string input, string firstName, string lastName)
    {
        var result = new PersianPersonNameParser().Parse(input);
        Assert.Equal(firstName, result.FirstName);
        Assert.Equal(lastName, result.LastName);
    }
}
