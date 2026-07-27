using Jellyfin.Plugin.Tagsmith.Tagging;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class CountryAliasCatalogTests
{
    [Theory]
    // The case that started this: every spelling collapses to one tag.
    [InlineData("United States", "united_states")]
    [InlineData("United States of America", "united_states")]
    [InlineData("USA", "united_states")]
    [InlineData("US", "united_states")]
    [InlineData("Estados Unidos", "united_states")]
    [InlineData("États-Unis", "united_states")]
    [InlineData("美国", "united_states")]
    // ISO codes.
    [InlineData("IND", "india")]
    [InlineData("DEU", "germany")]
    // Endonyms and other languages.
    [InlineData("Deutschland", "germany")]
    [InlineData("Allemagne", "germany")]
    [InlineData("日本", "japan")]
    [InlineData("Nippon", "japan")]
    [InlineData("Bharat", "india")]
    // Renamed states resolve to the current name.
    [InlineData("Burma", "myanmar")]
    [InlineData("Swaziland", "eswatini")]
    [InlineData("Czech Republic", "czechia")]
    [InlineData("Macedonia", "north_macedonia")]
    // Administrative long forms are shortened.
    [InlineData("Hong Kong", "hong_kong")]
    [InlineData("Palestine", "palestine")]
    [InlineData("Great Britain", "united_kingdom")]
    public void Resolves_to_canonical_slug(string input, string expected) =>
        Assert.Equal(expected, CountryAliasCatalog.Resolve(input));

    [Theory]
    // Not ISO 3166-1 territories — these must keep their own identity rather than being
    // folded into a successor state.
    [InlineData("Soviet Union", "soviet_union")]
    [InlineData("Yugoslavia", "yugoslavia")]
    [InlineData("West Germany", "west_germany")]
    [InlineData("Czechoslovakia", "czechoslovakia")]
    public void Unknown_territories_pass_through(string input, string expected)
    {
        Assert.False(CountryAliasCatalog.IsKnown(input));
        Assert.Equal(expected, CountryAliasCatalog.Resolve(input));
    }

    [Fact]
    public void Resolution_is_idempotent()
    {
        var once = CountryAliasCatalog.Resolve("United States of America");
        Assert.Equal(once, CountryAliasCatalog.Resolve(once));
    }

    [Fact]
    public void Catalog_is_populated() => Assert.True(CountryAliasCatalog.Count > 10_000);

    [Fact]
    public void Empty_input_yields_empty() => Assert.Equal(string.Empty, CountryAliasCatalog.Resolve(null));
}
