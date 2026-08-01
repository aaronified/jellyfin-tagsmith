using Jellyfin.Plugin.Tagsmith.Collections;
using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.Tagging;
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
        // Over every kind, not a hand-written list: a new kind added to the enum and missed
        // here would leave the test passing while no longer testing what it says.
        var configuration = new PluginConfiguration();

        foreach (var kind in Kinds)
        {
            Assert.False(TagGrouping.IsEnabled(kind, configuration), $"{kind} defaults on");
        }
    }

    // ------------------------------------------------------------ exhaustiveness

    /// <summary>
    /// Every switch over <see cref="ProjectionKind"/> must name every kind. The default arms
    /// are the dangerous part: <c>IsEnabled</c> answering false for a kind somebody forgot
    /// makes the projection loop treat it as switched off, and with <em>remove collections
    /// when disabled</em> set that tears down its library and box sets on every run — logged
    /// as though the user had asked for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_kind_has_a_namespace_a_library_name_and_an_enabled_flag(ProjectionKind kind)
    {
        var configuration = new PluginConfiguration();

        Assert.False(string.IsNullOrWhiteSpace(TagGrouping.NamespaceFor(kind, configuration)), $"{kind} has no namespace");
        Assert.False(string.IsNullOrWhiteSpace(TagGrouping.LibraryNameFor(kind, configuration)), $"{kind} has no library name");

        TagGrouping.SetEnabled(kind, configuration, true);
        Assert.True(TagGrouping.IsEnabled(kind, configuration), $"{kind} cannot be switched on");

        TagGrouping.SetEnabled(kind, configuration, false);
        Assert.False(TagGrouping.IsEnabled(kind, configuration), $"{kind} cannot be switched off");
    }

    [Fact]
    public void Every_kind_has_a_distinct_default_namespace_and_library_name()
    {
        // Two projections sharing a namespace build two identical libraries that then
        // overwrite each other's artwork, and two sharing a library name means the second
        // is refused as a name conflict. Neither may be true out of the box.
        var configuration = new PluginConfiguration();

        Assert.Equal(Kinds.Length, Kinds.Select(k => TagGrouping.NamespaceFor(k, configuration)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Kinds.Length, Kinds.Select(k => TagGrouping.LibraryNameFor(k, configuration)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ------------------------------------------------------------ award naming

    [Theory]
    [InlineData("oscar:best_picture", "Oscar – Best Picture")]
    [InlineData("bafta:best_film", "BAFTA – Best Film")]
    [InlineData("golden_globe:best_motion_picture_musical_or_comedy", "Golden Globe – Best Motion Picture Musical or Comedy")]
    [InlineData("emmy:outstanding_drama_series", "Emmy – Outstanding Drama Series")]
    [InlineData("oscar:best_makeup_and_hairstyling", "Oscar – Best Makeup and Hairstyling")]
    [InlineData("golden_globe:best_tv_series_drama", "Golden Globe – Best TV Series Drama")]
    [InlineData("golden_globe:best_limited_series_or_tv_film", "Golden Globe – Best Limited Series or TV Film")]
    public void Award_values_are_named_by_ceremony_and_category(string value, string expected)
    {
        Assert.Equal(expected, TagGrouping.DisplayName(ProjectionKind.Award, value));
        Assert.Equal(expected, TagGrouping.DisplayName(ProjectionKind.Nomination, value));
    }

    /// <summary>
    /// An en dash, not the colon the tag uses. <c>SanitiseName</c> replaces a colon with a
    /// space, so "Oscar: Best Picture" would reach the disk as "Oscar  Best Picture [boxset]"
    /// and Jellyfin would derive the collection's name from that.
    /// </summary>
    [Fact]
    public void An_award_name_survives_becoming_a_folder_name()
    {
        var name = TagGrouping.DisplayName(ProjectionKind.Award, "oscar:best_picture");
        var folder = BoxSetFolder.FolderNameFor(name);

        Assert.Equal("Oscar – Best Picture [boxset]", folder);
        Assert.DoesNotContain("  ", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_award_value_in_the_dataset_produces_a_usable_folder_name()
    {
        // Across the whole shipped dataset, not a sample: one value that sanitises to
        // nothing, or two that sanitise alike, costs a collection silently — CreateBoxSetFolder
        // logs and skips it.
        var names = AwardValues
            .Select(v => TagGrouping.DisplayName(ProjectionKind.Award, v))
            .ToArray();

        Assert.NotEmpty(names);
        Assert.All(names, name =>
        {
            var folder = BoxSetFolder.FolderNameFor(name);
            Assert.NotNull(folder);
            Assert.DoesNotContain("  ", folder!, StringComparison.Ordinal);
        });

        // Distinct names, so no two categories fight over one folder.
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_award_category_renders_a_known_acronym_as_a_word()
    {
        // Categories have no per-value name table — there are eighty of them — so the only
        // thing between the dataset and a collection called "Best Tv Series Drama" is the
        // acronym list. These are folder names as well as titles, so correcting one after
        // release renames a directory and recreates a box set.
        //
        // Asserts on the rendered output rather than on the acronym table, so it stays a
        // real check rather than restating the implementation. It cannot catch an acronym
        // nobody has thought of yet; a dataset regenerated with new categories still wants
        // reading.
        var words = AwardValues
            .Select(v => TagGrouping.DisplayName(ProjectionKind.Award, v))
            .SelectMany(name => name.Split(' '))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain("Tv", words, StringComparer.Ordinal);
        Assert.DoesNotContain("Bbc", words, StringComparer.Ordinal);
        Assert.DoesNotContain("Uk", words, StringComparer.Ordinal);
        Assert.DoesNotContain("Usa", words, StringComparer.Ordinal);
    }

    [Fact]
    public void A_value_with_no_ceremony_is_named_like_any_other_slug() =>
        // A tag added by hand under the award namespace. Ownership is by namespace, so it is
        // projected like a generated one and must still produce a sane name.
        Assert.Equal("Palme Dor", TagGrouping.DisplayName(ProjectionKind.Award, "palme_dor"));

    // ------------------------------------------------------------ list naming

    [Theory]
    [InlineData("imdb_top_250", "IMDb Top 250")]
    [InlineData("criterion_collection", "The Criterion Collection")]
    [InlineData("tspdt_1000", "TSPDT Top 1000")]
    [InlineData("national_film_registry", "National Film Registry")]
    public void List_values_get_their_real_names(string value, string expected) =>
        Assert.Equal(expected, TagGrouping.DisplayName(ProjectionKind.List, value));

    [Fact]
    public void Every_shipped_list_has_a_name_of_its_own()
    {
        // The fallback is title-casing, which turns imdb_top_250 into "Imdb Top 250" and
        // tspdt_1000 into "Tspdt 1000". A list added to the dataset and the settings page but
        // not to the name table would ship looking like that, and nothing else would notice.
        var slugs = ListSlugs;

        Assert.NotEmpty(slugs);
        Assert.All(slugs, slug =>
        {
            Assert.Contains(slug, TagGrouping.NamedLists);
            Assert.NotNull(BoxSetFolder.FolderNameFor(TagGrouping.DisplayName(ProjectionKind.List, slug)));
        });
    }

    [Fact]
    public void Every_shipped_ceremony_is_named_without_a_slug_showing_through()
    {
        var ceremonies = AwardValues
            .Select(v => v[..v.IndexOf(':', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(ceremonies);
        Assert.All(ceremonies, ceremony =>
        {
            Assert.Contains(ceremony, TagGrouping.NamedCeremonies);

            var name = TagGrouping.DisplayName(ProjectionKind.Award, ceremony + ":best_film");

            Assert.EndsWith(" – Best Film", name, StringComparison.Ordinal);
            Assert.DoesNotContain("_", name, StringComparison.Ordinal);
        });
    }

    private static readonly ProjectionKind[] Kinds = Enum.GetValues<ProjectionKind>();

    /// <summary>Every distinct <c>ceremony:category</c> value in the shipped dataset.</summary>
    private static string[] AwardValues =>
        CuratedData.AllAwards
            .SelectMany(a => a.Value.Wins.Concat(a.Value.Nominations))
            .Where(v => v.Contains(':', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every distinct list slug in the shipped dataset.</summary>
    private static string[] ListSlugs =>
        CuratedData.AllLists.SelectMany(l => l.Value).Distinct(StringComparer.Ordinal).ToArray();

    public static TheoryData<ProjectionKind> AllKinds()
    {
        var data = new TheoryData<ProjectionKind>();
        foreach (var kind in Kinds)
        {
            data.Add(kind);
        }

        return data;
    }
}
