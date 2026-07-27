using Jellyfin.Plugin.Tagsmith.Collections;
using Jellyfin.Plugin.Tagsmith.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// Ownership decisions. These guard the two failures that cost the user data: adopting a
/// library Tagsmith did not create and later deleting it, and mistaking a rename for a
/// deletion.
/// </summary>
public class LibraryOwnershipTests
{
    private const string MediaPath = "/config/data/tagsmith-origin";
    private const string OtherId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OurId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static LibraryView Folder(string name, string id, params string[] locations) =>
        new(name, id, locations);

    private static ManagedLibrary Record(string name, string id, string? configured = null) =>
        new()
        {
            Kind = ProjectionKind.Origin,
            Name = name,
            ItemId = id,
            ConfiguredName = configured ?? name
        };

    private static LibraryPlan Decide(ManagedLibrary? record, string configuredName, params LibraryView[] folders) =>
        LibraryOwnership.Decide(record, configuredName, MediaPath, folders);

    // ------------------------------------------------------------ creation

    [Fact]
    public void Creates_when_nothing_exists() =>
        Assert.Equal(LibraryAction.Create, Decide(null, "Origins").Action);

    [Fact]
    public void The_configured_name_is_sanitised_before_anything_else() =>
        Assert.Equal("Origins Countries", Decide(null, "Origins/Countries").Name);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_name_is_refused(string? name) =>
        Assert.Equal(LibraryAction.Invalid, Decide(null, name!).Action);

    // ------------------------------------------------------------ never adopt by name

    [Fact]
    public void A_library_of_the_users_own_with_the_same_name_is_never_adopted()
    {
        // 0.0.4 matched GetVirtualFolders() by name and recorded ownership by name only, so
        // an existing "Origins" was claimed — and RemoveVirtualFolder would later destroy it
        // along with every per-user access grant.
        var plan = Decide(null, "Origins", Folder("Origins", OtherId, "/media/films"));

        Assert.Equal(LibraryAction.NameConflict, plan.Action);
        Assert.Null(plan.Folder);
    }

    [Fact]
    public void A_name_conflict_survives_sanitisation()
    {
        var plan = Decide(null, "Origins/Countries", Folder("Origins Countries", OtherId, "/media/films"));
        Assert.Equal(LibraryAction.NameConflict, plan.Action);
    }

    [Fact]
    public void A_library_pointing_at_our_own_directory_is_ours_even_without_a_record()
    {
        // Self-healing: the private per-projection directory is proof of authorship, so a
        // lost record does not mean a second library next run.
        var plan = Decide(null, "Origins", Folder("Origins", OurId, MediaPath));

        Assert.Equal(LibraryAction.Use, plan.Action);
        Assert.Equal(OurId, plan.Folder!.Value.ItemId);
    }

    // ------------------------------------------------------------ rename, not deletion

    [Fact]
    public void A_rename_in_the_dashboard_is_followed_not_treated_as_a_deletion()
    {
        // POST /Library/VirtualFolders/Name exists, so users can rename. Matching by name
        // made that indistinguishable from a deletion: the projection was silently disabled
        // and every collection record forgotten.
        var plan = Decide(Record("Origins", OurId), "Origins", Folder("Countries", OurId, MediaPath));

        Assert.Equal(LibraryAction.Use, plan.Action);
        Assert.Equal("Countries", plan.Folder!.Value.Name);
    }

    [Fact]
    public void Ownership_follows_the_id_not_the_name()
    {
        var plan = Decide(
            Record("Origins", OurId),
            "Origins",
            Folder("Origins", OtherId, "/media/films"),
            Folder("Countries", OurId, MediaPath));

        Assert.Equal(LibraryAction.Use, plan.Action);
        Assert.Equal(OurId, plan.Folder!.Value.ItemId);
    }

    // ------------------------------------------------------------ deletion out of band

    [Fact]
    public void A_library_deleted_in_Jellyfin_is_abandoned_not_recreated()
    {
        var plan = Decide(Record("Origins", OurId), "Origins", Folder("Films", OtherId, "/media/films"));
        Assert.Equal(LibraryAction.Abandoned, plan.Action);
    }

    [Fact]
    public void Abandonment_needs_no_other_libraries_to_exist() =>
        Assert.Equal(LibraryAction.Abandoned, Decide(Record("Origins", OurId), "Origins").Action);

    // ------------------------------------------------------------ configured rename

    [Fact]
    public void Changing_the_name_in_Tagsmiths_own_settings_rebuilds()
    {
        var plan = Decide(Record("Origins", OurId), "Countries", Folder("Origins", OurId, MediaPath));

        Assert.Equal(LibraryAction.Rebuild, plan.Action);
        Assert.Equal("Countries", plan.Name);
    }

    [Fact]
    public void A_dashboard_rename_alone_does_not_rebuild()
    {
        // Jellyfin says "Countries", settings still say "Origins": follow, do not rebuild.
        var plan = Decide(Record("Countries", OurId, configured: "Origins"), "Origins", Folder("Countries", OurId, MediaPath));
        Assert.Equal(LibraryAction.Use, plan.Action);
    }

    // ------------------------------------------------------------ migration

    [Fact]
    public void A_pre_0_0_5_record_with_no_id_is_matched_by_media_path()
    {
        var legacy = new ManagedLibrary { Kind = ProjectionKind.Origin, Name = "Origins" };
        var plan = Decide(legacy, "Origins", Folder("Origins", OurId, MediaPath));

        Assert.Equal(LibraryAction.Use, plan.Action);
        Assert.Equal(OurId, plan.Folder!.Value.ItemId);
    }

    [Fact]
    public void A_pre_0_0_5_record_is_not_rebuilt_just_because_it_has_no_configured_name()
    {
        var legacy = new ManagedLibrary { Kind = ProjectionKind.Origin, Name = "Origins", ItemId = OurId };
        Assert.Equal(LibraryAction.Use, Decide(legacy, "Countries", Folder("Origins", OurId, MediaPath)).Action);
    }

    [Fact]
    public void A_pre_0_0_5_record_whose_library_is_gone_is_still_abandoned()
    {
        var legacy = new ManagedLibrary { Kind = ProjectionKind.Origin, Name = "Origins" };
        Assert.Equal(LibraryAction.Abandoned, Decide(legacy, "Origins").Action);
    }

    [Fact]
    public void A_stale_id_is_healed_from_the_media_path()
    {
        var plan = Decide(Record("Origins", "cccccccccccccccccccccccccccccccc"), "Origins", Folder("Origins", OurId, MediaPath));

        Assert.Equal(LibraryAction.Use, plan.Action);
        Assert.Equal(OurId, plan.Folder!.Value.ItemId);
    }

    // ------------------------------------------------------------ path matching

    [Theory]
    [InlineData("/config/data/tagsmith-origin")]
    [InlineData("/config/data/tagsmith-origin/")]
    [InlineData("\\config\\data\\tagsmith-origin")]
    [InlineData("/config/data/TAGSMITH-ORIGIN")]
    public void Media_paths_match_across_separators_and_case(string location) =>
        Assert.True(LibraryOwnership.PointsAt(Folder("Origins", OurId, location), MediaPath));

    [Theory]
    [InlineData("/config/data/tagsmith-lang")]
    [InlineData("/config/data")]
    [InlineData("/config/data/tagsmith-origin-2")]
    public void A_different_directory_is_not_a_match(string location) =>
        Assert.False(LibraryOwnership.PointsAt(Folder("Origins", OurId, location), MediaPath));

    [Fact]
    public void A_library_with_no_locations_matches_nothing() =>
        Assert.False(LibraryOwnership.PointsAt(Folder("Origins", OurId), MediaPath));
}
