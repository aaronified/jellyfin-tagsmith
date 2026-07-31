namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Which of Tagsmith's three triggers is asking about artwork.
/// </summary>
/// <remarks>
/// Artwork used to move in both directions on every projection run, which meant a poster set
/// in the library UI was not backed up until the next full sync, and that the heavy nightly
/// pass re-examined the poster on every collection it owned. The three triggers now do one
/// thing each and do not overlap. See <see cref="ArtworkPolicy.Decide"/>.
/// </remarks>
public enum ArtworkMode
{
    /// <summary>
    /// The scheduled projection run. Artwork is applied to the items this run created, to
    /// items with no poster at all, and to items whose stored artwork file changed on disk
    /// since Tagsmith last applied it — never over a poster the user set by hand. Adoption
    /// never happens here.
    /// </summary>
    ScheduledRun,

    /// <summary>
    /// The <em>Reapply collection artwork</em> action. Pushes the thumbnails folder onto
    /// every item Tagsmith owns, whatever poster is on it.
    /// </summary>
    ReapplyFromFolder,

    /// <summary>
    /// The <c>ItemUpdated</c> listener. Copies a poster somebody else set into the thumbnails
    /// folder, and never writes to the item.
    /// </summary>
    AdoptOnly
}

/// <summary>
/// What to do about one item's artwork.
/// </summary>
public enum ArtworkAction
{
    /// <summary>Leave the item and the thumbnails folder alone.</summary>
    None,

    /// <summary>
    /// Copy the file in the thumbnails folder onto the item, unless that same file is
    /// already the one Tagsmith applied.
    /// </summary>
    Apply,

    /// <summary>
    /// Copy the file in the thumbnails folder onto the item regardless of what is there,
    /// discarding a poster set by hand.
    /// </summary>
    Reapply,

    /// <summary>
    /// Copy the item's poster into the thumbnails folder, unless it is the poster Tagsmith
    /// itself applied.
    /// </summary>
    Adopt
}

/// <summary>
/// What the scheduled run knows about one item's artwork when it has to decide. All four
/// facts are cheap to compute and none of them require touching the item.
/// </summary>
/// <param name="CreatedThisRun">Whether the run that is asking created the item.</param>
/// <param name="HasArtworkFile">Whether the thumbnails folder holds a file for this item.</param>
/// <param name="HasPoster">Whether the item currently carries a primary image.</param>
/// <param name="PosterIsOwn">
/// Whether the poster on the item is the one Tagsmith itself applied, decided by comparing
/// the poster's hash against <c>AppliedImageHash</c>. A poster that is not Tagsmith's is the
/// user's, and only the explicit reapply action may touch it.
/// </param>
/// <param name="FileChanged">
/// Whether the artwork file's hash differs from <c>ImageHash</c>, i.e. the user dropped a
/// new image into the thumbnails folder since Tagsmith last applied one.
/// </param>
public readonly record struct ArtworkFacts(
    bool CreatedThisRun,
    bool HasArtworkFile,
    bool HasPoster,
    bool PosterIsOwn,
    bool FileChanged);

/// <summary>
/// Maps a trigger and one item onto a single artwork action, so the "which trigger does
/// what" rule is one readable table that can be tested without a server.
/// </summary>
public static class ArtworkPolicy
{
    /// <summary>
    /// Decides what a trigger should do about one item's artwork.
    /// </summary>
    /// <param name="mode">Which trigger is asking.</param>
    /// <param name="facts">What is currently true of the item and its artwork file.</param>
    /// <returns>The action to take.</returns>
    public static ArtworkAction Decide(ArtworkMode mode, ArtworkFacts facts) => mode switch
    {
        // The scheduled run applies the folder wherever that cannot destroy user intent:
        // an item it just created, an item with no poster at all, or its own poster gone
        // stale because the source file changed. A poster the user set by hand was adopted
        // into the folder the moment it was set, so leaving it alone loses nothing.
        ArtworkMode.ScheduledRun when !facts.HasArtworkFile => ArtworkAction.None,

        // Even a collection created this run can already carry a poster that is not
        // Tagsmith's: recreating a box set whose folder survived a failed delete has the
        // local image provider pick the old poster.png back up. The PosterIsOwn guard
        // applies here exactly as it does for existing collections.
        ArtworkMode.ScheduledRun when facts.CreatedThisRun && (!facts.HasPoster || facts.PosterIsOwn)
            => ArtworkAction.Apply,
        ArtworkMode.ScheduledRun when facts.CreatedThisRun => ArtworkAction.None,

        // Reapply, not Apply: with no poster on the item the hash short-circuit inside the
        // apply must not win. The record can still carry the file's hash — the user deleted
        // the poster in the UI, or the box set was torn down and recreated — and skipping
        // here would leave the item permanently blank.
        ArtworkMode.ScheduledRun when !facts.HasPoster => ArtworkAction.Reapply,
        ArtworkMode.ScheduledRun when facts.PosterIsOwn && facts.FileChanged => ArtworkAction.Apply,
        ArtworkMode.ScheduledRun => ArtworkAction.None,

        // "Collections with no matching file keep what they have" — the reapply button
        // reads the folder, it does not blank posters the folder cannot replace.
        ArtworkMode.ReapplyFromFolder => facts.HasArtworkFile ? ArtworkAction.Reapply : ArtworkAction.None,

        // An item created a moment ago carries no poster, so there is nothing to adopt; the
        // listener never sees one either, since it only ever runs off an ItemUpdated for an
        // item already recorded as Tagsmith's.
        ArtworkMode.AdoptOnly => facts.CreatedThisRun ? ArtworkAction.None : ArtworkAction.Adopt,
        _ => ArtworkAction.None
    };
}
