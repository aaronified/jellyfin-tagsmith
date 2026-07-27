using Jellyfin.Plugin.Tagsmith.Configuration;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// What Tagsmith should do about a projection's library on this run.
/// </summary>
public enum LibraryAction
{
    /// <summary>The configured name is unusable; do nothing and log.</summary>
    Invalid,

    /// <summary>No library exists and the name is free; create one.</summary>
    Create,

    /// <summary>A library Tagsmith owns was found; use it.</summary>
    Use,

    /// <summary>
    /// The name configured in Tagsmith's own settings changed. Jellyfin 10.11 exposes no
    /// rename on <see cref="MediaBrowser.Controller.Library.ILibraryManager"/>, so the
    /// library is torn down and rebuilt.
    /// </summary>
    Rebuild,

    /// <summary>
    /// A library Tagsmith owned is gone from Jellyfin. Treat as intent to disable; never
    /// recreate it.
    /// </summary>
    Abandoned,

    /// <summary>
    /// The wanted name is taken by a library Tagsmith does not own. Refuse, loudly. Adopting
    /// it would mean a later teardown destroys a library the user built.
    /// </summary>
    NameConflict
}

/// <summary>
/// The parts of <c>VirtualFolderInfo</c> the ownership rules need, so the rules can be
/// tested without a server.
/// </summary>
/// <param name="Name">The library name as Jellyfin currently reports it.</param>
/// <param name="ItemId">The <c>CollectionFolder</c> id, the ownership token.</param>
/// <param name="Locations">The library's media paths.</param>
public readonly record struct LibraryView(string? Name, string? ItemId, IReadOnlyList<string> Locations);

/// <summary>
/// The result of <see cref="LibraryOwnership.Decide"/>.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Name">The configured name after sanitisation.</param>
/// <param name="Folder">The library involved, when there is one.</param>
public readonly record struct LibraryPlan(LibraryAction Action, string Name, LibraryView? Folder);

/// <summary>
/// Decides whether a library belongs to Tagsmith, purely from the recorded ownership and
/// what Jellyfin reports.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is by <b>id</b>, never by name. Matching on name meant an existing library the
/// user happened to call "Origins" was silently adopted and could later be destroyed by
/// <c>RemoveVirtualFolder</c>, taking its definition and every per-user access grant with it;
/// and it meant a rename in Dashboard → Libraries (<c>POST /Library/VirtualFolders/Name</c>)
/// was indistinguishable from a deletion, so a rename quietly disabled the projection.
/// </para>
/// <para>
/// The media path is a secondary, self-healing signal. Each projection owns a private
/// directory under the Jellyfin data path, so a library pointing at it is Tagsmith's by
/// construction. That is what migrates a pre-0.0.5 record, which recorded only a name.
/// </para>
/// </remarks>
public static class LibraryOwnership
{
    /// <summary>
    /// Works out what to do about a projection's library.
    /// </summary>
    /// <param name="record">What Tagsmith recorded, or null if it has never made one.</param>
    /// <param name="configuredName">The library name from settings, unsanitised.</param>
    /// <param name="mediaPath">The private directory this projection's library points at.</param>
    /// <param name="folders">Every virtual folder Jellyfin reports.</param>
    public static LibraryPlan Decide(
        ManagedLibrary? record,
        string? configuredName,
        string mediaPath,
        IReadOnlyList<LibraryView> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var wanted = BoxSetFolder.SanitiseName(configuredName);
        if (wanted.Length == 0)
        {
            return new LibraryPlan(LibraryAction.Invalid, wanted, null);
        }

        LibraryView? found = null;

        if (record is not null && !string.IsNullOrEmpty(record.ItemId))
        {
            found = FirstOrNull(folders, f =>
                string.Equals(f.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase));
        }

        // Heals a record whose id went stale, and migrates a pre-0.0.5 record that has none.
        found ??= FirstOrNull(folders, f => PointsAt(f, mediaPath));

        if (record is null)
        {
            if (found is not null)
            {
                return new LibraryPlan(LibraryAction.Use, wanted, found);
            }

            return FirstOrNull(folders, f => string.Equals(f.Name, wanted, StringComparison.Ordinal)) is not null
                ? new LibraryPlan(LibraryAction.NameConflict, wanted, null)
                : new LibraryPlan(LibraryAction.Create, wanted, null);
        }

        if (found is null)
        {
            return new LibraryPlan(LibraryAction.Abandoned, wanted, null);
        }

        // An empty ConfiguredName is a pre-0.0.5 record: adopt and backfill rather than
        // tearing down a library that is perfectly good.
        if (!string.IsNullOrEmpty(record.ConfiguredName)
            && !string.Equals(record.ConfiguredName, wanted, StringComparison.Ordinal))
        {
            return new LibraryPlan(LibraryAction.Rebuild, wanted, found);
        }

        return new LibraryPlan(LibraryAction.Use, wanted, found);
    }

    /// <summary>
    /// Returns whether a library serves the given media path.
    /// </summary>
    public static bool PointsAt(LibraryView folder, string mediaPath)
    {
        if (folder.Locations is null || string.IsNullOrEmpty(mediaPath))
        {
            return false;
        }

        var wanted = Normalise(mediaPath);
        foreach (var location in folder.Locations)
        {
            if (string.Equals(Normalise(location), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalise(string? path) =>
        string.IsNullOrEmpty(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimEnd('/');

    private static LibraryView? FirstOrNull(IReadOnlyList<LibraryView> folders, Func<LibraryView, bool> predicate)
    {
        foreach (var folder in folders)
        {
            if (predicate(folder))
            {
                return folder;
            }
        }

        return null;
    }
}
