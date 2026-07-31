using Jellyfin.Plugin.Tagsmith.External;
using Jellyfin.Plugin.Tagsmith.Tagging;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// The pure parts of the external-metadata layer: how source answers merge, and how the
/// odd language codes normalise. The reflection surface itself is pinned against the
/// server source in the sources' remarks and exercised through <see cref="Reflected"/>.
/// </summary>
public class ExternalMetadataTests
{
    // ------------------------------------------------------------ merging

    [Fact]
    public void The_first_source_wins_per_field()
    {
        var tmdb = new ExternalItemInfo("bn", ["IN"]);
        var tvdb = new ExternalItemInfo("eng", ["usa"]);

        var merged = ExternalItemInfo.Merge(tmdb, tvdb);

        Assert.Equal("bn", merged.OriginalLanguage);
        Assert.Equal(["IN"], merged.Countries);
    }

    [Fact]
    public void A_later_source_fills_the_gaps_the_first_left()
    {
        var tmdb = new ExternalItemInfo(null, ["IN"]);
        var tvdb = new ExternalItemInfo("ben", []);

        var merged = ExternalItemInfo.Merge(tmdb, tvdb);

        Assert.Equal("ben", merged.OriginalLanguage);
        Assert.Equal(["IN"], merged.Countries);
    }

    [Fact]
    public void Merging_into_nothing_returns_the_answer_itself()
    {
        var info = new ExternalItemInfo("bn", ["IN"]);

        Assert.Same(info, ExternalItemInfo.Merge(null, info));
    }

    [Theory]
    [InlineData(null, 0, true, false)]
    [InlineData("", 0, true, false)]
    [InlineData("bn", 0, false, false)]
    [InlineData(null, 1, false, false)]
    [InlineData("bn", 1, false, true)]
    public void Empty_and_complete_describe_the_two_stopping_conditions(
        string? language,
        int countryCount,
        bool empty,
        bool complete)
    {
        var info = new ExternalItemInfo(language, Enumerable.Repeat("IN", countryCount).ToArray());

        Assert.Equal(empty, info.IsEmpty);
        Assert.Equal(complete, info.IsComplete);
    }

    // ------------------------------------------------------------ language codes

    [Theory]
    [InlineData("zhtw", "zh")]   // TVDb's private id for Traditional Chinese
    [InlineData("ZHTW", "zh")]
    [InlineData("por", "pt")]    // TVDb's European Portuguese
    [InlineData("pt", "pt")]
    [InlineData("bn", "bn")]     // TMDb ISO 639-1 passes through
    [InlineData("ben", "ben")]   // stream 639-2 passes through
    [InlineData(" eng ", "eng")]
    public void Language_code_quirks_normalise(string code, string expected) =>
        Assert.Equal(expected, LanguageCodes.Normalise(code));

    [Theory]
    [InlineData("xx", true)]     // TMDb's "no language"
    [InlineData("XX", true)]
    [InlineData("zxx", true)]    // ISO 639-2 "no linguistic content"
    [InlineData("bn", false)]
    public void A_silent_film_carries_no_language_tag(string code, bool untagged) =>
        Assert.Equal(untagged, LanguageCodes.IsNoLanguage(code));

    [Theory]
    [InlineData("cn", "Cantonese")]   // TMDb's Cantonese; the server's tables cannot resolve it
    [InlineData("yue", "Cantonese")]
    [InlineData("bn", null)]
    public void Codes_the_server_cannot_resolve_get_display_overrides(string code, string? expected) =>
        Assert.Equal(expected, LanguageCodes.DisplayOverride(code));

    // ------------------------------------------------------------ reflection plumbing

    private sealed class Shape
    {
        public string? Name { get; set; } = "India";

        public int Id { get; set; } = 42;
    }

    [Fact]
    public void Properties_read_off_unknown_types()
    {
        var target = new Shape();

        Assert.Equal("India", Reflected.GetString(target, "Name"));
        Assert.Equal(42, Reflected.Get(target, "Id"));
        Assert.Null(Reflected.Get(target, "DoesNotExist"));
        Assert.Null(Reflected.Get(null, "Name"));
    }

    [Fact]
    public async Task A_reflected_task_result_is_awaited_not_blocked_on()
    {
        // The sources invoke Task-returning methods through MethodInfo and get object back.
        var result = await Reflected.ResultOf(Task.FromResult(new Shape()));

        Assert.Equal("India", Reflected.GetString(result, "Name"));
        Assert.Null(await Reflected.ResultOf(null));
        Assert.Null(await Reflected.ResultOf("not a task"));
    }

    [Fact]
    public async Task A_faulted_reflected_task_surfaces_its_exception()
    {
        var task = Task.FromException<Shape>(new InvalidOperationException("tmdb down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Reflected.ResultOf(task));
    }

    [Fact]
    public void Methods_bind_by_exact_parameter_list()
    {
        Assert.NotNull(Reflected.Method(typeof(string), "IndexOf", typeof(char)));
        Assert.Null(Reflected.Method(typeof(string), "NoSuchMethod", typeof(char)));
    }
}
