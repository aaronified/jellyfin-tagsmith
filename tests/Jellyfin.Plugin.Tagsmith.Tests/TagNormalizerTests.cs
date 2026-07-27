using Jellyfin.Plugin.Tagsmith.Tagging;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class TagNormalizerTests
{
    [Theory]
    [InlineData("India", "india")]
    [InlineData("  United States  ", "united_states")]
    [InlineData("Côte d'Ivoire", "cote_d_ivoire")]
    [InlineData("Bosnia & Herzegovina", "bosnia_herzegovina")]
    [InlineData("Türkiye", "turkiye")]
    [InlineData("---", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Slug_normalises(string? input, string expected) =>
        Assert.Equal(expected, TagNormalizer.Slug(input));

    [Fact]
    public void Slug_keeps_non_latin_scripts() =>
        Assert.Equal("日本", TagNormalizer.Slug("日本"));

    [Fact]
    public void Compose_builds_namespaced_tag() =>
        Assert.Equal("origin=india", TagNormalizer.Compose("origin", "=", "India"));

    [Fact]
    public void Compose_returns_null_for_empty_value() =>
        Assert.Null(TagNormalizer.Compose("origin", "=", "   "));
}
