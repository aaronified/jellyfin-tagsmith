using Jellyfin.Plugin.Tagsmith.Collections;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// A projection's media directory must never be empty when Jellyfin looks at it.
/// </summary>
/// <remarks>
/// <c>Folder.IsLibraryFolderAccessible</c> skips a top-level library folder whose directory
/// has no entries, so no <c>Folder</c> row is created, so <c>CollectionFolder.PhysicalFolderIds</c>
/// stays empty, so <c>GetTopParentIdsForQuery</c> answers every client request for that
/// library with an unmatchable GUID. The library renders as an empty shelf however many box
/// sets exist in the database, and nothing is logged. This is what left the Languages and
/// Decades libraries permanently empty, so the rule is pinned rather than left to a comment.
/// </remarks>
public class MediaDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tagsmith-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_root, "tagsmith-origin");

    [Fact]
    public void Seeding_creates_the_directory()
    {
        MediaDirectory.Seed(Path_);
        Assert.True(Directory.Exists(Path_));
    }

    [Fact]
    public void A_seeded_directory_is_never_empty()
    {
        MediaDirectory.Seed(Path_);

        // The exact predicate Jellyfin applies: DirectoryService.IsAccessible is
        // GetFileSystemEntryPaths(path).Any().
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path_));
    }

    [Fact]
    public void Seeding_reports_whether_it_wrote_anything()
    {
        Assert.True(MediaDirectory.Seed(Path_));
        Assert.False(MediaDirectory.Seed(Path_));
    }

    /// <summary>
    /// The condition is "the marker is missing", not "the directory is empty" — otherwise a
    /// projection built by an earlier version, whose directory already holds box sets, is
    /// never marked, and the run that removes its last value leaves the directory empty and
    /// unmarked. That is exactly the state Jellyfin discards.
    /// </summary>
    [Fact]
    public void A_directory_that_already_holds_box_sets_is_still_marked()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path_, "France [boxset]"));

        Assert.True(MediaDirectory.Seed(Path_));
        Assert.True(File.Exists(System.IO.Path.Combine(Path_, MediaDirectory.MarkerName)));
    }

    [Fact]
    public void A_directory_emptied_of_its_last_collection_is_still_not_empty()
    {
        var boxSet = System.IO.Path.Combine(Path_, "France [boxset]");
        Directory.CreateDirectory(boxSet);
        MediaDirectory.Seed(Path_);

        Directory.Delete(boxSet, true);

        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path_));
    }

    [Fact]
    public void The_marker_is_a_dotfile_so_no_resolver_claims_it()
    {
        // BoxSetResolver keys on "[boxset]" in the name or a collection.xml beside it, and
        // the video resolvers key on a media extension. A dot-prefixed file is neither, and
        // Jellyfin's own ignore rules skip it.
        Assert.StartsWith(".", MediaDirectory.MarkerName, StringComparison.Ordinal);
        Assert.DoesNotContain("[boxset]", MediaDirectory.MarkerName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_directory_holding_only_the_marker_can_be_torn_down()
    {
        MediaDirectory.Seed(Path_);
        Assert.True(MediaDirectory.IsDisposable(Path_));
    }

    [Fact]
    public void A_directory_holding_a_box_set_cannot_be_torn_down()
    {
        MediaDirectory.Seed(Path_);
        Directory.CreateDirectory(System.IO.Path.Combine(Path_, "France [boxset]"));

        Assert.False(MediaDirectory.IsDisposable(Path_));
    }

    [Fact]
    public void A_directory_holding_anything_else_cannot_be_torn_down()
    {
        MediaDirectory.Seed(Path_);
        File.WriteAllText(System.IO.Path.Combine(Path_, "something-the-user-put-here.txt"), "hello");

        Assert.False(MediaDirectory.IsDisposable(Path_));
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_disposable() =>
        Assert.True(MediaDirectory.IsDisposable(Path_));

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
