using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// The tag lifecycle guarantees from docs/tagging.md, exercised against a stub library
/// manager. These are the first tests to run <see cref="TagSynchronizer"/> itself rather
/// than reading it — the pruning rules are where a bug deletes user data.
/// </summary>
/// <remarks>
/// <see cref="Plugin.Instance"/> is null under test, so <c>SyncAsync</c> falls back to a
/// fresh <see cref="PluginConfiguration"/> — which is why every test here drives the
/// provider through a fake and asserts on the computed tag set via the item.
/// </remarks>
public class TagSynchronizerTests
{
    /// <summary>
    /// A provider whose namespaces and tags are handed in directly, so the synchroniser's
    /// own behaviour is the only thing under test.
    /// </summary>
    private sealed class FakeProvider : ITagProvider
    {
        public required IReadOnlyCollection<string> Claimed { get; init; }

        public required IReadOnlyCollection<string> Tags { get; init; }

        public string Name => "Fake";

        public IReadOnlyCollection<string> Namespaces(PluginConfiguration configuration) => Claimed;

        public Task<IReadOnlyCollection<string>> GetTagsAsync(
            BaseItem item,
            PluginConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult(Tags);
    }

    private static (TagSynchronizer Synchronizer, ILibraryManager Library) Build(
        IReadOnlyCollection<string> claimed,
        IReadOnlyCollection<string> tags)
    {
        var library = Substitute.For<ILibraryManager>();
        var provider = new FakeProvider { Claimed = claimed, Tags = tags };
        var synchronizer = new TagSynchronizer(library, [provider], NullLogger<TagSynchronizer>.Instance);
        return (synchronizer, library);
    }

    private static Movie ItemWith(params string[] tags) => new()
    {
        Name = "Test Item",
        Tags = tags
    };

    [Fact]
    public async Task The_shipped_Urdu_alias_is_applied_without_any_user_configuration()
    {
        // Plugin.Instance is null here, so SyncAsync builds a default PluginConfiguration
        // with an empty Aliases array — exactly the state a user who has never opened the
        // settings page is in. The fold has to come from the shipped defaults or not at all.
        var (synchronizer, _) = Build(["lang"], ["lang=urdu"]);
        var item = ItemWith();

        await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.Contains("lang=hindi", item.Tags);
        Assert.DoesNotContain("lang=urdu", item.Tags);
    }

    [Fact]
    public async Task An_existing_Urdu_tag_is_rewritten_to_Hindi_on_the_next_sync()
    {
        // The migration path for a library tagged before the default shipped: the old value
        // is inside a managed namespace, so pruning takes it and the fold puts Hindi back.
        var (synchronizer, _) = Build(["lang"], ["lang=urdu"]);
        var item = ItemWith("lang=urdu");

        var changed = await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.True(changed);
        Assert.Contains("lang=hindi", item.Tags);
        Assert.DoesNotContain("lang=urdu", item.Tags);
    }

    [Fact]
    public async Task Tags_outside_managed_namespaces_are_never_touched()
    {
        var (synchronizer, _) = Build(["origin"], ["origin=india"]);
        var item = ItemWith("Favourites", "kids-safe");

        await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.Contains("Favourites", item.Tags);
        Assert.Contains("kids-safe", item.Tags);
        Assert.Contains("origin=india", item.Tags);
    }

    [Fact]
    public async Task A_changed_value_rewrites_its_tag_instead_of_adding_a_second_one()
    {
        var (synchronizer, _) = Build(["origin"], ["origin=united_states"]);
        var item = ItemWith("origin=united_states_of_america");

        await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.Contains("origin=united_states", item.Tags);
        Assert.DoesNotContain("origin=united_states_of_america", item.Tags);
    }

    [Fact]
    public async Task A_hand_added_tag_inside_a_managed_namespace_is_managed()
    {
        // Ownership is by namespace: metadata says Japan, the hand-added India goes.
        var (synchronizer, _) = Build(["origin"], ["origin=japan"]);
        var item = ItemWith("origin=india");

        await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.Contains("origin=japan", item.Tags);
        Assert.DoesNotContain("origin=india", item.Tags);
    }

    [Fact]
    public async Task An_unchanged_item_is_not_written()
    {
        var (synchronizer, library) = Build(["origin"], ["origin=india"]);
        var item = ItemWith("origin=india");

        var changed = await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.False(changed);
        await library.DidNotReceiveWithAnyArgs().UpdateItemAsync(default!, default!, default, default);
    }

    // ------------------------------------------------------------ blank prefixes

    [Fact]
    public async Task A_blank_namespace_claims_nothing()
    {
        // The catastrophic case: a claimed namespace of "" would make the prefix "=",
        // or with a blank separator "", and "" owns — and deletes — every tag on every
        // item. A blank namespace must be ignored outright.
        var (synchronizer, library) = Build([""], []);
        var item = ItemWith("Favourites", "list=to_watch");

        var changed = await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.False(changed);
        Assert.Contains("Favourites", item.Tags);
        Assert.Contains("list=to_watch", item.Tags);
        await library.DidNotReceiveWithAnyArgs().UpdateItemAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_whitespace_namespace_claims_nothing()
    {
        var (synchronizer, library) = Build(["   "], []);
        var item = ItemWith("Favourites");

        var changed = await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.False(changed);
        Assert.Contains("Favourites", item.Tags);
        await library.DidNotReceiveWithAnyArgs().UpdateItemAsync(default!, default!, default, default);
    }

    // ------------------------------------------------------------ pruning

    [Fact]
    public async Task A_value_no_longer_produced_is_pruned_from_its_namespace()
    {
        // The deselected-ceremony case: award= stays claimed, the oscar tags stop being
        // produced, and the pass removes them.
        var (synchronizer, _) = Build(["award"], ["award=bafta:best_film"]);
        var item = ItemWith("award=oscar:best_picture", "award=bafta:best_film", "Favourites");

        await synchronizer.SyncAsync(item, CancellationToken.None);

        Assert.DoesNotContain("award=oscar:best_picture", item.Tags);
        Assert.Contains("award=bafta:best_film", item.Tags);
        Assert.Contains("Favourites", item.Tags);
    }
}
