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
    // ISO codes that collide with some locale's display name for a different country used
    // to be dropped as ambiguous, tagging origin=ru while a film from the same country
    // tagged origin=russia. A code is unambiguous by definition and wins its own country.
    // These are the load-bearing path for TMDb origin_country and TVDb OriginalCountry.
    [InlineData("RU", "russia")]
    [InlineData("NGA", "nigeria")]
    [InlineData("AO", "angola")]
    [InlineData("AS", "american_samoa")]
    [InlineData("BI", "burundi")]
    [InlineData("KM", "comoros")]
    [InlineData("SA", "saudi_arabia")]
    [InlineData("IN", "india")]
    [InlineData("usa", "united_states")]   // TVDb sends lowercase alpha-3
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
    // Palestine, every way a metadata source spells it. The singular "Palestinian
    // Territory" is the one TMDb sends and the one that used to slip through as its own
    // tag, because CLDR only carries the plural.
    [InlineData("Palestinian Territory", "palestine")]
    [InlineData("Palestinian Territories", "palestine")]
    [InlineData("Palestinian Territory, Occupied", "palestine")]
    [InlineData("Occupied Palestinian Territory", "palestine")]
    [InlineData("State of Palestine", "palestine")]
    // The two Congos. The long names carried no mapping at all, so a film from Kinshasa
    // tagged itself rather than joining the collection for its own country.
    [InlineData("Democratic Republic of the Congo", "congo_kinshasa")]
    [InlineData("DR Congo", "congo_kinshasa")]
    [InlineData("DRC", "congo_kinshasa")]
    [InlineData("Zaire", "congo_kinshasa")]
    [InlineData("Republic of the Congo", "congo_brazzaville")]
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
    public void The_two_Congos_stay_apart() =>
        // Folding them would be worse than the gap that prompted this: they are different
        // countries. Bare "Congo" is left to CLDR, which reads it as Brazzaville — genuinely
        // ambiguous in English, and guessing the other way would be no better.
        Assert.NotEqual(
            CountryAliasCatalog.Resolve("Democratic Republic of the Congo"),
            CountryAliasCatalog.Resolve("Republic of the Congo"));

    [Fact]
    public void Empty_input_yields_empty() => Assert.Equal(string.Empty, CountryAliasCatalog.Resolve(null));
}
