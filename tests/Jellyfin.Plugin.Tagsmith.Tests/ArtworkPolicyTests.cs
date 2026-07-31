using Jellyfin.Plugin.Tagsmith.Collections;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// The three artwork triggers do one thing each and must not overlap. This pins the whole
/// table, because the failure mode when they do overlap is the scheduled run copying
/// Tagsmith's own poster over the user's curated artwork file.
/// </summary>
public class ArtworkPolicyTests
{
    /// <summary>
    /// A convenient baseline: the folder has a file for the item, the item already carries
    /// a poster, that poster is Tagsmith's own, and the file has not changed. Every test
    /// perturbs exactly the facts it is about.
    /// </summary>
    private static ArtworkFacts Steady(
        bool createdThisRun = false,
        bool hasArtworkFile = true,
        bool hasPoster = true,
        bool posterIsOwn = true,
        bool fileChanged = false,
        bool posterIsGenerated = false) =>
        new(createdThisRun, hasArtworkFile, hasPoster, posterIsOwn, fileChanged, posterIsGenerated);

    // ------------------------------------------------------------ scheduled run

    [Fact]
    public void The_scheduled_run_applies_artwork_to_a_collection_it_just_created() =>
        Assert.Equal(
            ArtworkAction.Apply,
            ArtworkPolicy.Decide(ArtworkMode.ScheduledRun, Steady(createdThisRun: true)));

    [Fact]
    public void The_scheduled_run_leaves_a_steady_collection_alone() =>
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(ArtworkMode.ScheduledRun, Steady()));

    [Fact]
    public void The_scheduled_run_fills_in_a_missing_poster()
    {
        // The images-copied-in-later case: the collection existed before the user populated
        // the thumbnails folder, so it has no poster and the next sync must give it one.
        // Reapply, not Apply — the record may still hash-match the file (poster deleted in
        // the UI, or the box set torn down and recreated) and the skip must not win.
        var action = ArtworkPolicy.Decide(ArtworkMode.ScheduledRun, Steady(hasPoster: false, posterIsOwn: false));

        Assert.Equal(ArtworkAction.Reapply, action);
    }

    [Fact]
    public void The_scheduled_run_refreshes_its_own_stale_poster() =>
        // The user dropped a new image over the old one in the thumbnails folder. The
        // poster on the collection is Tagsmith's, so replacing it destroys nothing.
        Assert.Equal(
            ArtworkAction.Apply,
            ArtworkPolicy.Decide(ArtworkMode.ScheduledRun, Steady(fileChanged: true)));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void The_scheduled_run_never_touches_a_poster_the_user_set_by_hand(bool createdThisRun, bool fileChanged) =>
        // Even when the folder file changed, and even on a collection created this run —
        // recreating a box set whose folder survived a failed delete resolves with the old
        // poster already on it. Only the explicit reapply button may discard user intent.
        // "User set" is narrower than "not Tagsmith's": see the generated-poster tests.
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(
                ArtworkMode.ScheduledRun,
                Steady(createdThisRun: createdThisRun, posterIsOwn: false, fileChanged: fileChanged)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_scheduled_run_does_nothing_without_an_artwork_file(bool createdThisRun) =>
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(
                ArtworkMode.ScheduledRun,
                Steady(createdThisRun: createdThisRun, hasArtworkFile: false, fileChanged: false)));

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    public void The_scheduled_run_never_adopts(bool createdThisRun, bool hasPoster, bool posterIsOwn, bool fileChanged) =>
        Assert.NotEqual(
            ArtworkAction.Adopt,
            ArtworkPolicy.Decide(
                ArtworkMode.ScheduledRun,
                Steady(createdThisRun: createdThisRun, hasPoster: hasPoster, posterIsOwn: posterIsOwn, fileChanged: fileChanged)));

    // ------------------------------------------------------------ generated posters

    /// <summary>
    /// The bug this whole fact exists for. Jellyfin's <c>CollectionImageProvider</c> copies
    /// the first member's poster onto a box set during the very refresh that resolves the
    /// folder Tagsmith wrote — and it does so <em>because</em> Tagsmith locks its collections,
    /// since its <c>Supports</c> returns false for an unlocked item. Every projected
    /// collection therefore has a poster before Tagsmith ever gets to apply one, and reading
    /// that as user intent left the artwork folder permanently vetoed.
    /// </summary>
    [Fact]
    public void The_scheduled_run_applies_over_a_poster_the_server_generated() =>
        Assert.Equal(
            ArtworkAction.Reapply,
            ArtworkPolicy.Decide(
                ArtworkMode.ScheduledRun,
                Steady(posterIsOwn: false, posterIsGenerated: true)));

    [Fact]
    public void The_scheduled_run_applies_over_a_generated_poster_even_when_the_file_is_unchanged() =>
        // Reapply rather than Apply, and this is the reason: the record can still hash-match
        // the artwork file from an apply that happened before the server generated over it,
        // so an Apply would short-circuit and the wrong poster would stick forever. This is
        // the row that un-sticks collections already broken on a live server.
        Assert.Equal(
            ArtworkAction.Reapply,
            ArtworkPolicy.Decide(
                ArtworkMode.ScheduledRun,
                Steady(posterIsOwn: false, fileChanged: false, posterIsGenerated: true)));

    [Fact]
    public void A_collection_created_this_run_takes_the_folder_over_a_generated_poster() =>
        Assert.Equal(
            ArtworkAction.Apply,
            ArtworkPolicy.Decide(
                ArtworkMode.ScheduledRun,
                Steady(createdThisRun: true, posterIsOwn: false, posterIsGenerated: true)));

    /// <summary>
    /// Adoption copies the poster on an item into the thumbnails folder as the stored
    /// artwork for that value. Doing that with a poster Jellyfin generated would overwrite
    /// the user's curated file with a copy of one member's poster — the exact data loss
    /// adoption exists to prevent.
    /// </summary>
    [Fact]
    public void The_listener_never_adopts_a_poster_the_server_generated() =>
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(
                ArtworkMode.AdoptOnly,
                Steady(posterIsOwn: false, posterIsGenerated: true)));

    [Fact]
    public void A_poster_that_is_both_Tagsmiths_and_in_the_metadata_tree_is_left_alone()
    {
        // Both facts can hold at once: a local save that fails falls back to the internal
        // metadata path, so Tagsmith's own poster ends up where a generated one lives. A
        // forced reapply there would re-save an identical image every night, and an
        // unchanged sync is supposed to be a no-op.
        var action = ArtworkPolicy.Decide(
            ArtworkMode.ScheduledRun,
            Steady(posterIsOwn: true, posterIsGenerated: true, fileChanged: false));

        Assert.Equal(ArtworkAction.None, action);
    }

    [Fact]
    public void A_generated_poster_is_never_treated_as_user_intent()
    {
        foreach (var facts in AllFacts().Where(f => f.PosterIsGenerated))
        {
            Assert.False(facts.PosterIsUserSet);
        }
    }

    [Fact]
    public void Only_a_poster_that_is_neither_Tagsmiths_nor_the_servers_counts_as_user_intent()
    {
        Assert.True(Steady(hasPoster: true, posterIsOwn: false, posterIsGenerated: false).PosterIsUserSet);
        Assert.False(Steady(hasPoster: false, posterIsOwn: false, posterIsGenerated: false).PosterIsUserSet);
        Assert.False(Steady(hasPoster: true, posterIsOwn: true, posterIsGenerated: false).PosterIsUserSet);
    }

    // ------------------------------------------------------------ reapply button

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_reapply_action_forces_the_folder_onto_every_collection(bool posterIsOwn) =>
        Assert.Equal(
            ArtworkAction.Reapply,
            ArtworkPolicy.Decide(ArtworkMode.ReapplyFromFolder, Steady(posterIsOwn: posterIsOwn)));

    // ------------------------------------------------------------ event listener

    [Fact]
    public void The_listener_adopts() =>
        Assert.Equal(
            ArtworkAction.Adopt,
            ArtworkPolicy.Decide(ArtworkMode.AdoptOnly, Steady(posterIsOwn: false)));

    [Fact]
    public void The_listener_has_nothing_to_adopt_from_a_collection_created_a_moment_ago() =>
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(ArtworkMode.AdoptOnly, Steady(createdThisRun: true)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_listener_never_writes_to_a_collection(bool createdThisRun)
    {
        var action = ArtworkPolicy.Decide(ArtworkMode.AdoptOnly, Steady(createdThisRun: createdThisRun, posterIsOwn: false));

        Assert.NotEqual(ArtworkAction.Apply, action);
        Assert.NotEqual(ArtworkAction.Reapply, action);
    }

    // ------------------------------------------------------------ no overlap

    [Fact]
    public void Exactly_one_trigger_adopts()
    {
        var adopting = AllModes()
            .Where(m => AllFacts().Any(f => ArtworkPolicy.Decide(m, f) == ArtworkAction.Adopt))
            .ToArray();

        Assert.Single(adopting);
        Assert.Equal(ArtworkMode.AdoptOnly, adopting[0]);
    }

    [Fact]
    public void Every_mode_does_something()
    {
        // A mode added later without a row in the table falls through to None for every
        // combination of facts, which is a trigger that quietly stops touching artwork.
        foreach (var mode in AllModes())
        {
            Assert.Contains(AllFacts().Select(f => ArtworkPolicy.Decide(mode, f)), a => a != ArtworkAction.None);
        }
    }

    [Fact]
    public void Nothing_is_ever_applied_without_a_file_to_apply()
    {
        // Apply and Reapply both dereference the artwork file; a verdict that reaches them
        // with HasArtworkFile false is a crash in the synchroniser. Adoption is exempt —
        // it reads the poster, not the folder.
        foreach (var mode in AllModes())
        {
            foreach (var facts in AllFacts().Where(f => !f.HasArtworkFile))
            {
                var action = ArtworkPolicy.Decide(mode, facts);
                Assert.True(
                    action is ArtworkAction.None or ArtworkAction.Adopt,
                    $"{mode} with no artwork file decided {action}");
            }
        }
    }

    private static IEnumerable<ArtworkMode> AllModes() => Enum.GetValues<ArtworkMode>();

    /// <summary>
    /// Every combination of the six facts. Sixty-four cases is cheap, and exhaustive beats
    /// clever here.
    /// </summary>
    private static IEnumerable<ArtworkFacts> AllFacts() =>
        from created in new[] { false, true }
        from hasFile in new[] { false, true }
        from hasPoster in new[] { false, true }
        from own in new[] { false, true }
        from changed in new[] { false, true }
        from generated in new[] { false, true }
        select new ArtworkFacts(created, hasFile, hasPoster, own, changed, generated);

    // ------------------------------------------------------------ the event filter

    /// <summary>
    /// The server declares <c>ItemUpdateType.None = 1</c>, not 0, and an update carries
    /// several reasons at once. Both make the obvious filters wrong.
    /// </summary>
    [Theory]
    [InlineData(ItemUpdateType.ImageUpdate, true)]
    [InlineData(ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataEdit, true)]
    [InlineData(ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataImport, true)]
    [InlineData(ItemUpdateType.MetadataEdit, false)]
    [InlineData(ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataImport, false)]
    [InlineData(ItemUpdateType.None, false)]
    public void Only_an_image_change_reaches_the_adoption_path(ItemUpdateType reason, bool expected) =>
        Assert.Equal(expected, PosterAdoptionListener.IsImageChange(reason));

    [Fact]
    public void None_is_not_zero() =>
        // Pinned because it is surprising, and because a `reason == 0` or `HasFlag(None)`
        // test would read backwards if it ever changed.
        Assert.Equal(1, (int)ItemUpdateType.None);
}
