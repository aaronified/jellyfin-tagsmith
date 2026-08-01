using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.External;
using Jellyfin.Plugin.Tagsmith.Providers;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// The server answers <c>FindLanguageInfo</c> with ISO 639-2's scholarly headings, not with
/// the names people use. Every input below is a verbatim <c>DisplayName</c> from Jellyfin
/// 10.11.11's <c>iso6392.txt</c>.
/// </summary>
public class LanguageCodesTests
{
    [Theory]
    // Semicolons separate synonyms, most common first. The one that prompted this.
    [InlineData("Spanish; Castilian", "Spanish")]
    [InlineData("Dutch; Flemish", "Dutch")]
    [InlineData("Romanian; Moldavian; Moldovan", "Romanian")]
    [InlineData("Panjabi; Punjabi", "Panjabi")]
    [InlineData("Pushto; Pashto", "Pushto")]
    [InlineData("Sinhala; Sinhalese", "Sinhala")]
    [InlineData("Catalan; Valencian", "Catalan")]
    [InlineData("Gaelic; Scottish Gaelic", "Gaelic")]
    [InlineData("Church Slavic; Old Slavonic; Church Slavonic; Old Bulgarian; Old Church Slavonic", "Church Slavic")]
    // Parentheses hold a qualifier: a script, a region, a date range.
    [InlineData("Chinese (Traditional)", "Chinese")]
    [InlineData("Chinese (Simplified)", "Chinese")]
    [InlineData("Portuguese (Brazil)", "Portuguese")]
    [InlineData("Portuguese (Portugal)", "Portuguese")]
    [InlineData("Norwegian (Bokmal)", "Norwegian")]
    [InlineData("Norwegian (Nynorsk)", "Norwegian")]
    [InlineData("French (Canada)", "French")]
    [InlineData("Interlingua (International Auxiliary Language Association)", "Interlingua")]
    // A single comma is an inverted heading, so the halves swap rather than truncate.
    [InlineData("Greek, Modern (1453-)", "Modern Greek")]
    [InlineData("Greek, Ancient (to 1453)", "Ancient Greek")]
    [InlineData("Sotho, Southern", "Southern Sotho")]
    [InlineData("English, Old (ca.450-1100)", "Old English")]
    // Both at once.
    [InlineData("Ndebele, South; South Ndebele", "South Ndebele")]
    [InlineData("Ndebele, North; North Ndebele", "North Ndebele")]
    // Plain names are left exactly as they are.
    [InlineData("Bengali", "Bengali")]
    [InlineData("Japanese", "Japanese")]
    [InlineData("Hindi", "Hindi")]
    public void Compound_names_reduce_to_the_language(string displayName, string expected) =>
        Assert.Equal(expected, LanguageCodes.Simplify(displayName));

    [Fact]
    public void The_two_Ndebeles_stay_apart() =>
        // Truncating at the comma instead of swapping would read as correct on "Greek,
        // Modern" and quietly merge these two, which are different languages.
        Assert.NotEqual(
            LanguageCodes.Simplify("Ndebele, South; South Ndebele"),
            LanguageCodes.Simplify("Ndebele, North; North Ndebele"));

    [Fact]
    public void The_two_Greeks_stay_apart() =>
        Assert.NotEqual(
            LanguageCodes.Simplify("Greek, Modern (1453-)"),
            LanguageCodes.Simplify("Greek, Ancient (to 1453)"));

    [Theory]
    // The swap is not universally flattering. Asserted verbatim rather than waved at, so
    // the awkward output is on the record: these three rows carry no two-letter code, and
    // LocalizationManager.LoadCultures skips any row without one, so FindLanguageInfo can
    // never return them. If that ever changes, this is where to look.
    [InlineData("Creoles and pidgins, English based", "English based Creoles and pidgins")]
    [InlineData("Creoles and pidgins, French-based ", "French-based Creoles and pidgins")]
    [InlineData("Creoles and pidgins, Portuguese-based ", "Portuguese-based Creoles and pidgins")]
    public void An_inverted_heading_swaps_even_when_it_reads_oddly(string displayName, string expected) =>
        Assert.Equal(expected, LanguageCodes.Simplify(displayName));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(1453-)")]
    [InlineData(";")]
    public void A_name_that_reduces_to_nothing_is_left_alone(string displayName)
    {
        // Better a clumsy tag than no tag: an empty return would compose to a bare prefix,
        // and TagNormalizer would drop the tag entirely.
        var simplified = LanguageCodes.Simplify(displayName);

        Assert.Equal(displayName.Trim(), simplified);
    }

    [Fact]
    public void Simplifying_is_idempotent()
    {
        foreach (var name in new[] { "Spanish; Castilian", "Greek, Modern (1453-)", "Chinese (Traditional)", "Bengali" })
        {
            var once = LanguageCodes.Simplify(name);
            Assert.Equal(once, LanguageCodes.Simplify(once));
        }
    }

    [Fact]
    public void The_simplified_name_still_slugs_to_something()
    {
        foreach (var name in new[] { "Spanish; Castilian", "Greek, Modern (1453-)", "Ndebele, South; South Ndebele" })
        {
            Assert.NotEqual(string.Empty, TagNormalizer.Slug(LanguageCodes.Simplify(name)));
        }
    }

    [Fact]
    public void Spanish_now_matches_the_artwork_file_that_ships() =>
        // assets/thumbnails/lang/spanish.png has always existed; spanish_castilian never
        // matched it, so the Spanish collection came up with no poster.
        Assert.Equal("spanish", TagNormalizer.Slug(LanguageCodes.Simplify("Spanish; Castilian")));

    // ------------------------------------------------------------ the existing rules

    [Theory]
    [InlineData("xx")]
    [InlineData("zxx")]
    [InlineData("XX")]
    public void No_language_codes_produce_no_tag(string code) =>
        Assert.True(LanguageCodes.IsNoLanguage(code));

    [Theory]
    [InlineData("cn", "Cantonese")]
    [InlineData("yue", "Cantonese")]
    // Greek overrides the correct-but-unhelpful "Modern Greek" that the comma rule produces.
    // `el` is the only Greek the server can return — `grc` has no two-letter code, so
    // LoadCultures never yields it — which makes the qualifier pure noise.
    [InlineData("el", "Greek")]
    [InlineData("ell", "Greek")]
    [InlineData("gre", "Greek")]
    public void Codes_the_table_answers_badly_are_overridden(string code, string expected) =>
        Assert.Equal(expected, LanguageCodes.DisplayOverride(code));

    [Fact]
    public void The_override_wins_over_the_comma_rule() =>
        // Both mechanisms are live and they disagree about `el` on purpose: Simplify is
        // right in general, the override is right about this one code, and DisplayLanguage
        // consults the override first.
        Assert.NotEqual(
            LanguageCodes.DisplayOverride("el"),
            LanguageCodes.Simplify("Greek, Modern (1453-)"));

    [Theory]
    [InlineData("zhtw", "zh")]
    [InlineData("por", "pt")]
    [InlineData("bn", "bn")]
    public void Source_quirk_codes_fold_to_the_base_language(string code, string expected) =>
        Assert.Equal(expected, LanguageCodes.Normalise(code));

    // ------------------------------------------------------------ the wiring

    /// <summary>
    /// Simplification is only worth anything if the provider actually applies it, and this
    /// is the one place the two meet. The localisation manager is substituted with the
    /// answers Jellyfin 10.11.11's own table gives, so a refactor that stops calling
    /// <see cref="LanguageCodes.Simplify"/> fails here rather than shipping.
    /// </summary>
    [Theory]
    [InlineData("es", "Spanish; Castilian", "lang=spanish")]
    [InlineData("nl", "Dutch; Flemish", "lang=dutch")]
    [InlineData("ro", "Romanian; Moldavian; Moldovan", "lang=romanian")]
    [InlineData("zh", "Chinese (Traditional)", "lang=chinese")]
    [InlineData("bn", "Bengali", "lang=bengali")]
    public async Task The_provider_tags_the_simplified_name(string code, string tableAnswer, string expected)
    {
        var tags = await TagsForOriginalLanguage(code, tableAnswer);

        Assert.Contains(expected, tags, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_provider_tags_Greek_rather_than_Modern_Greek()
    {
        // The override path, which bypasses the table entirely.
        var tags = await TagsForOriginalLanguage("el", "Greek, Modern (1453-)");

        Assert.Contains("lang=greek", tags, StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs the real provider over one movie whose original language the external source
    /// reports as <paramref name="code"/>, with the server's table stubbed to answer
    /// <paramref name="tableAnswer"/>.
    /// </summary>
    private static async Task<IReadOnlyCollection<string>> TagsForOriginalLanguage(string code, string tableAnswer)
    {
        var localization = Substitute.For<ILocalizationManager>();
        localization.FindLanguageInfo(Arg.Any<string>())
            .Returns(new CultureDto(tableAnswer, tableAnswer, code, [code]));

        var source = Substitute.For<IExternalMetadataSource>();
        source.Name.Returns("Fake");
        source.GetAsync(Arg.Any<MediaBrowser.Controller.Entities.BaseItem>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalItemInfo(code, []));

        var provider = new CoreMetadataTagProvider(
            Substitute.For<IMediaSourceManager>(),
            localization,
            Substitute.For<ILibraryManager>(),
            [source],
            NullLogger<CoreMetadataTagProvider>.Instance);

        var configuration = new PluginConfiguration
        {
            EnableOrigin = false,
            EnableLanguage = true,
            EnableYear = false,
            EnableAudioLanguage = false
        };

        return await provider.GetTagsAsync(new Movie { Name = "Test" }, configuration, CancellationToken.None);
    }
}
