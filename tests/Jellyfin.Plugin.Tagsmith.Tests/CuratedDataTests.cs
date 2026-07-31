using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.Providers;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// Pins the shape and a few stable facts of the embedded award and list datasets, and the
/// providers that read them. The golden records are chosen for being historically settled —
/// The Godfather's Best Picture win is not going to change under a dataset regeneration.
/// </summary>
public class CuratedDataTests
{
    // ------------------------------------------------------------ datasets

    [Fact]
    public void The_awards_dataset_is_populated() => Assert.True(CuratedData.AwardTitleCount > 3_000);

    [Fact]
    public void The_lists_dataset_is_populated() => Assert.True(CuratedData.ListTitleCount > 2_000);

    [Fact]
    public void The_godfather_won_best_picture()
    {
        var record = CuratedData.AwardsFor("tt0068646");

        Assert.NotNull(record);
        Assert.Contains("oscar:best_picture", record.Wins);

        // A winner is also a nominee, so the nominee set is complete on its own.
        Assert.Contains("oscar:best_picture", record.Nominations);
    }

    [Fact]
    public void A_nomination_without_a_win_stays_a_nomination()
    {
        // The Shawshank Redemption: seven nominations, zero wins — as settled as data gets.
        var record = CuratedData.AwardsFor("tt0111161");

        Assert.NotNull(record);
        Assert.Contains("oscar:best_picture", record.Nominations);
        Assert.DoesNotContain("oscar:best_picture", record.Wins);
    }

    [Fact]
    public void Citizen_kane_sits_on_the_expected_lists()
    {
        var lists = CuratedData.ListsFor("tt0033467");

        Assert.Contains("afi_100", lists);
        Assert.Contains("national_film_registry", lists);
        Assert.Contains("tspdt_1000", lists);
    }

    [Fact]
    public void Lookup_is_case_and_whitespace_tolerant() =>
        Assert.NotNull(CuratedData.AwardsFor("  TT0068646  "));

    [Fact]
    public void An_unknown_or_missing_id_resolves_to_nothing()
    {
        Assert.Null(CuratedData.AwardsFor("tt0000000"));
        Assert.Null(CuratedData.AwardsFor(null));
        Assert.Empty(CuratedData.ListsFor(""));
    }

    [Fact]
    public void Every_award_value_in_the_dataset_is_ceremony_prefixed_and_slug_shaped()
    {
        // The values ARE the tag schema; a malformed one would ship a malformed tag to
        // every affected library. This sweeps the whole dataset — it is an in-memory
        // dictionary, and a regeneration accident anywhere in it is what this exists to
        // catch.
        foreach (var (id, record) in CuratedData.AllAwards)
        {
            Assert.Matches(@"^tt\d+$", id);

            foreach (var value in record.Wins.Concat(record.Nominations))
            {
                var parts = value.Split(':');
                Assert.Equal(2, parts.Length);
                Assert.Contains(parts[0], (string[])["oscar", "bafta", "golden_globe", "emmy"]);
                Assert.Matches("^[a-z0-9_]+$", parts[1]);
            }

            // A win is a nomination; the generator records it in both sets.
            foreach (var win in record.Wins)
            {
                Assert.Contains(win, record.Nominations);
            }
        }
    }

    [Fact]
    public void Every_list_value_in_the_dataset_is_a_known_list()
    {
        string[] known =
        [
            "imdb_top_250", "sight_and_sound", "afi_100", "bfi_top_100",
            "national_film_registry", "criterion_collection", "tspdt_1000"
        ];

        foreach (var (id, memberOf) in CuratedData.AllLists)
        {
            Assert.Matches(@"^tt\d+$", id);
            Assert.NotEmpty(memberOf);

            foreach (var list in memberOf)
            {
                Assert.Contains(list, known);
            }
        }
    }

    [Fact]
    public void An_imdb_id_wrapped_in_a_url_still_resolves() =>
        // NFO writers sometimes store the full IMDb URL as the provider id.
        Assert.NotNull(CuratedData.AwardsFor("https://www.imdb.com/title/tt0068646/"));

    // ------------------------------------------------------------ providers

    private static PluginConfiguration Config(Action<PluginConfiguration>? mutate = null)
    {
        var configuration = new PluginConfiguration();
        mutate?.Invoke(configuration);
        return configuration;
    }

    private static Movie GodfatherLike() => new()
    {
        Name = "The Godfather",
        ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0068646" }
    };

    [Fact]
    public async Task Awards_and_nominations_are_separate_namespaces()
    {
        var provider = new AwardTagProvider();
        var configuration = Config(c =>
        {
            c.EnableAwards = true;
            c.EnableNominations = true;
        });

        var tags = await provider.GetTagsAsync(GodfatherLike(), configuration, CancellationToken.None);

        Assert.Contains("award=oscar:best_picture", tags);
        Assert.Contains("nomination=oscar:best_picture", tags);
    }

    [Fact]
    public async Task Disabled_ceremonies_produce_nothing()
    {
        var provider = new AwardTagProvider();
        var configuration = Config(c =>
        {
            c.EnableAwards = true;
            c.AwardCeremonies = ["bafta"];
        });

        var tags = await provider.GetTagsAsync(GodfatherLike(), configuration, CancellationToken.None);

        Assert.DoesNotContain(tags, t => t.StartsWith("award=oscar:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Awards_off_means_no_tags_and_no_claimed_namespaces()
    {
        var provider = new AwardTagProvider();
        var configuration = Config();

        Assert.Empty(provider.Namespaces(configuration));
        Assert.Empty(await provider.GetTagsAsync(GodfatherLike(), configuration, CancellationToken.None));
    }

    [Fact]
    public void Each_award_toggle_claims_its_own_namespace()
    {
        var provider = new AwardTagProvider();

        Assert.Equal(["award"], provider.Namespaces(Config(c => c.EnableAwards = true)));
        Assert.Equal(["nomination"], provider.Namespaces(Config(c => c.EnableNominations = true)));
    }

    [Fact]
    public async Task Only_selected_lists_produce_tags()
    {
        var provider = new ListTagProvider();
        var kane = new Movie
        {
            Name = "Citizen Kane",
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0033467" }
        };

        var configuration = Config(c => c.EnabledLists = ["afi_100"]);
        var tags = await provider.GetTagsAsync(kane, configuration, CancellationToken.None);

        Assert.Contains("list=afi_100", tags);
        Assert.DoesNotContain(tags, t => t.Contains("national_film_registry", StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_namespace_is_only_active_while_lists_are_selected()
    {
        var provider = new ListTagProvider();

        Assert.Empty(provider.Namespaces(Config()));
        Assert.Equal(["list"], provider.Namespaces(Config(c => c.EnabledLists = ["imdb_top_250"])));
    }

    [Fact]
    public async Task An_item_without_an_imdb_id_gets_nothing()
    {
        var provider = new AwardTagProvider();
        var configuration = Config(c => c.EnableAwards = true);

        var tags = await provider.GetTagsAsync(new Movie { Name = "Unmatched" }, configuration, CancellationToken.None);

        Assert.Empty(tags);
    }
}
