using SmsPanel.Api.Services;

namespace SmsPanel.Tests;

public sealed class PersonNameMatcherTests
{
    [Theory]
    [InlineData("داور مفهومی نیاری", "داور مفهومي نيارى")]
    [InlineData("حسین سهرابی", "حسین\r\nسهرابی")]
    [InlineData("محمد رضا احمدی", "احمدی محمد رضا")]
    public void Matches_normalizes_common_persian_variants_and_spacing(string expected, string actual)
    {
        Assert.True(PersonNameMatcher.Matches(expected, actual));
    }

    [Fact]
    public void Matches_accepts_similarity_at_or_above_ninety_percent()
    {
        Assert.True(PersonNameMatcher.SimilarityScore("عبدالرضا حسینی", "عبدالرضا حسینیی") >= 0.90);
        Assert.True(PersonNameMatcher.Matches("عبدالرضا حسینی", "عبدالرضا حسینیی"));
    }

    [Fact]
    public void Matches_rejects_materially_different_people()
    {
        Assert.False(PersonNameMatcher.Matches("حسین سهرابی", "حسین طهرانی"));
    }

    [Fact]
    public void Parts_separates_newlines_and_drops_no_name_characters()
    {
        Assert.Equal(["حسین", "سهرابی", "Ir540570032380010027887001", "بانک", "پاسارگاد"], PersonNameMatcher.Parts("حسین سهرابی\r\nIr540570032380010027887001 بانک پاسارگاد"));
    }
}
