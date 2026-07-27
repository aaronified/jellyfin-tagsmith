using Jellyfin.Plugin.Tagsmith.Collections;
using Jellyfin.Plugin.Tagsmith.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class TagGroupingTests
{
    [Theory]
    [InlineData("1954", "1950s")]
    [InlineData("1950", "1950s")]
    [InlineData("1959", "1950s")]
    [InlineData("1960", "1960s")]
    [InlineData("2001", "2000s")]
    public void Years_roll_up_to_decades(string year, string expected) =>
        Assert.Equal(expected, TagGrouping.ToDecade(year));

    [Theory]
    [InlineData("")]
    [InlineData("nineteen")]
    [InlineData("54")]
    [InlineData("-1950")]
    public void Non_years_do_not_produce_a_decade(string value) =>
        Assert.Null(TagGrouping.ToDecade(value));

    [Fact]
    public void Year_projection_groups_by_decade_while_the_tag_stays_precise() =>
        Assert.Equal("1950s", TagGrouping.ValueFor(ProjectionKind.Year, "year=1954", "year", "="));

    [Fact]
    public void Other_projections_use_the_value_verbatim() =>
        Assert.Equal("united_states", TagGrouping.ValueFor(ProjectionKind.Origin, "origin=united_states", "origin", "="));

    [Theory]
    [InlineData("lang=bengali")]
    [InlineData("origin")]
    [InlineData("origin=")]
    [InlineData("")]
    public void Tags_outside_the_projection_are_skipped(string tag) =>
        Assert.Null(TagGrouping.ValueFor(ProjectionKind.Origin, tag, "origin", "="));

    [Fact]
    public void Prefix_match_is_case_insensitive() =>
        Assert.Equal("india", TagGrouping.ValueFor(ProjectionKind.Origin, "ORIGIN=india", "origin", "="));

    [Theory]
    [InlineData("india", "India")]
    [InlineData("united_states", "United States")]
    [InlineData("hong_kong", "Hong Kong")]
    [InlineData("1950s", "1950s")]
    [InlineData("", "")]
    public void Display_names_are_humanised(string value, string expected) =>
        Assert.Equal(expected, TagGrouping.DisplayName(value));

    [Fact]
    public void Namespace_rename_follows_the_configured_value()
    {
        var configuration = new PluginConfiguration { OriginNamespace = "country" };

        Assert.Equal("country", TagGrouping.NamespaceFor(ProjectionKind.Origin, configuration));
        Assert.Equal("india", TagGrouping.ValueFor(ProjectionKind.Origin, "country=india", "country", "="));
    }

    [Fact]
    public void Projections_are_off_by_default()
    {
        var configuration = new PluginConfiguration();

        Assert.False(TagGrouping.IsEnabled(ProjectionKind.Origin, configuration));
        Assert.False(TagGrouping.IsEnabled(ProjectionKind.Language, configuration));
        Assert.False(TagGrouping.IsEnabled(ProjectionKind.Year, configuration));
    }
}
