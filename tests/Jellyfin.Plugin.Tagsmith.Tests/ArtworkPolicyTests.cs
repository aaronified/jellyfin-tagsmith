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
    // ------------------------------------------------------------ scheduled run

    [Fact]
    public void The_scheduled_run_applies_artwork_to_a_collection_it_just_created() =>
        Assert.Equal(
            ArtworkAction.Apply,
            ArtworkPolicy.Decide(ArtworkMode.NewCollections, createdThisRun: true));

    [Fact]
    public void The_scheduled_run_leaves_a_collection_that_already_existed_alone() =>
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(ArtworkMode.NewCollections, createdThisRun: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_scheduled_run_never_adopts(bool createdThisRun) =>
        Assert.NotEqual(
            ArtworkAction.Adopt,
            ArtworkPolicy.Decide(ArtworkMode.NewCollections, createdThisRun));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_scheduled_run_never_forces(bool createdThisRun) =>
        // Applying to a new collection still respects the recorded hash. Only the button
        // discards a poster somebody set.
        Assert.NotEqual(
            ArtworkAction.Reapply,
            ArtworkPolicy.Decide(ArtworkMode.NewCollections, createdThisRun));

    // ------------------------------------------------------------ reapply button

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_reapply_action_forces_the_folder_onto_every_collection(bool createdThisRun) =>
        Assert.Equal(
            ArtworkAction.Reapply,
            ArtworkPolicy.Decide(ArtworkMode.ReapplyFromFolder, createdThisRun));

    // ------------------------------------------------------------ event listener

    [Fact]
    public void The_listener_adopts() =>
        Assert.Equal(
            ArtworkAction.Adopt,
            ArtworkPolicy.Decide(ArtworkMode.AdoptOnly, createdThisRun: false));

    [Fact]
    public void The_listener_has_nothing_to_adopt_from_a_collection_created_a_moment_ago() =>
        Assert.Equal(
            ArtworkAction.None,
            ArtworkPolicy.Decide(ArtworkMode.AdoptOnly, createdThisRun: true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_listener_never_writes_to_a_collection(bool createdThisRun)
    {
        var action = ArtworkPolicy.Decide(ArtworkMode.AdoptOnly, createdThisRun);

        Assert.NotEqual(ArtworkAction.Apply, action);
        Assert.NotEqual(ArtworkAction.Reapply, action);
    }

    // ------------------------------------------------------------ no overlap

    [Fact]
    public void Exactly_one_trigger_adopts()
    {
        var adopting = AllModes()
            .Where(m => ArtworkPolicy.Decide(m, false) == ArtworkAction.Adopt
                        || ArtworkPolicy.Decide(m, true) == ArtworkAction.Adopt)
            .ToArray();

        Assert.Single(adopting);
        Assert.Equal(ArtworkMode.AdoptOnly, adopting[0]);
    }

    [Fact]
    public void Every_mode_does_something()
    {
        // A mode added later without a row in the table falls through to None for both
        // cases, which is a trigger that quietly stops touching artwork at all.
        foreach (var mode in AllModes())
        {
            Assert.Contains(
                new[] { ArtworkPolicy.Decide(mode, true), ArtworkPolicy.Decide(mode, false) },
                a => a != ArtworkAction.None);
        }
    }

    private static IEnumerable<ArtworkMode> AllModes() => Enum.GetValues<ArtworkMode>();

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
