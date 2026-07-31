namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// The private directory one projection's library points at, and the one rule Jellyfin
/// imposes on it: <b>it must never be empty</b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>LibraryManager.AddVirtualFolder</c> ends by awaiting <c>ValidateTopLibraryFolders</c>,
/// which resolves the <c>.mblink</c> target into a <c>Folder</c> item — but only after
/// <c>Folder.IsLibraryFolderAccessible</c> approves it, and for a top-level library folder
/// that check is <c>DirectoryService.IsAccessible</c>, which is literally "the directory has
/// at least one entry":
/// </para>
/// <code>
/// // Folder.cs — for top parents i.e. Library folders, skip the validation if it's empty
/// if (item.IsTopParent &amp;&amp; !directoryService.IsAccessible(item.ContainingFolderPath))
/// {
///     Logger.LogWarning("Library folder {LibraryFolderPath} is inaccessible or empty, skipping", …);
///     return false;
/// }
///
/// // DirectoryService.cs
/// public bool IsAccessible(string path) =&gt; _fileSystem.GetFileSystemEntryPaths(path).Any();
/// </code>
/// <para>
/// Everything the user sees hangs off the row that check gates. <c>CollectionFolder</c> is
/// not a real container — its own <c>ValidateChildrenInternal</c> returns a completed task
/// and its <c>Children</c> are projected through <c>PhysicalFolderIds</c>, which
/// <c>RefreshLinkedChildren</c> fills by matching the library's locations against the
/// physical folders that exist. No row, no <c>PhysicalFolderIds</c>. And
/// <c>LibraryManager.GetTopParentIdsForQuery</c> answers a client's request for a
/// <c>CollectionFolder</c>'s contents with exactly those ids, substituting a freshly
/// generated GUID when the list is empty so that the query matches nothing by construction.
/// The library then renders as an empty shelf in every client however many box sets exist in
/// the database, and there is no warning and no error.
/// </para>
/// <para>
/// Jellyfin's own Collections library gets away with being created against an empty
/// directory only because <c>IsLibraryFolderAccessible</c> hard-codes an exemption for a
/// folder named <c>collections</c>. <c>tagsmith-origin</c> gets no exemption, so Tagsmith
/// has to keep the directory non-empty itself.
/// </para>
/// </remarks>
public static class MediaDirectory
{
    /// <summary>
    /// The file kept in every projection's media directory so Jellyfin never sees it empty.
    /// </summary>
    /// <remarks>
    /// Dot-prefixed so no resolver claims it: <c>BoxSetResolver</c> wants <c>[boxset]</c> in
    /// the name or a <c>collection.xml</c> beside it, and the video resolvers want a media
    /// extension. It is a file rather than a directory for the same reason.
    /// </remarks>
    public const string MarkerName = ".tagsmith-library";

    private const string MarkerText =
        "Tagsmith keeps this file here so Jellyfin does not discard the library as empty.\n"
        + "Deleting it makes every collection in this library disappear.\n";

    /// <summary>
    /// Creates the directory if it is missing and makes sure it is not empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be called <b>before</b> <c>AddVirtualFolder</c>, not after: the check that
    /// discards an empty folder runs inside that call. It costs one <c>File.Exists</c> in the
    /// steady state.
    /// </para>
    /// <para>
    /// The condition is "the marker is missing", not "the directory is empty". A directory
    /// that currently holds box set folders is not empty <em>today</em>, but the run that
    /// removes its last value would leave it empty and unmarked — and the library would be
    /// discarded the next time Jellyfin looked at it. Seeding only what is already empty
    /// would also never repair a projection built by an earlier version, which is the case
    /// this exists for.
    /// </para>
    /// </remarks>
    /// <param name="path">The projection's media directory.</param>
    /// <returns>Whether the marker was written.</returns>
    public static bool Seed(string path)
    {
        Directory.CreateDirectory(path);

        var marker = Path.Combine(path, MarkerName);
        if (File.Exists(marker))
        {
            return false;
        }

        File.WriteAllText(marker, MarkerText);
        return true;
    }

    /// <summary>
    /// Whether the directory holds nothing but Tagsmith's own marker, and can therefore be
    /// removed when a projection is torn down.
    /// </summary>
    /// <remarks>
    /// The marker does not count as content. Without this exception the marker would keep
    /// every torn-down projection's directory alive for ever, and the next projection to
    /// reuse the path would inherit whatever else was left in it.
    /// </remarks>
    /// <param name="path">The projection's media directory.</param>
    public static bool IsDisposable(string path) =>
        !Directory.Exists(path)
        || !Directory.EnumerateFileSystemEntries(path)
            .Any(entry => !string.Equals(Path.GetFileName(entry), MarkerName, StringComparison.Ordinal));
}
