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
    /// The scheduled projection run. Artwork is applied to the collections this run created
    /// and to nothing else; adoption never happens here.
    /// </summary>
    NewCollections,

    /// <summary>
    /// The <em>Reapply collection artwork</em> action. Pushes the thumbnails folder onto
    /// every collection Tagsmith owns, whatever poster is on it.
    /// </summary>
    ReapplyFromFolder,

    /// <summary>
    /// The <c>ItemUpdated</c> listener. Copies a poster somebody else set into the thumbnails
    /// folder, and never writes to the collection.
    /// </summary>
    AdoptOnly
}

/// <summary>
/// What to do about one collection's artwork.
/// </summary>
public enum ArtworkAction
{
    /// <summary>Leave the collection and the thumbnails folder alone.</summary>
    None,

    /// <summary>
    /// Copy the file in the thumbnails folder onto the collection, unless that same file is
    /// already the one Tagsmith applied.
    /// </summary>
    Apply,

    /// <summary>
    /// Copy the file in the thumbnails folder onto the collection regardless of what is
    /// there, discarding a poster set by hand.
    /// </summary>
    Reapply,

    /// <summary>
    /// Copy the collection's poster into the thumbnails folder, unless it is the poster
    /// Tagsmith itself applied.
    /// </summary>
    Adopt
}

/// <summary>
/// Maps a trigger and one collection onto a single artwork action, so the "which trigger
/// does what" rule is one readable table that can be tested without a server.
/// </summary>
public static class ArtworkPolicy
{
    /// <summary>
    /// Decides what a trigger should do about one collection's artwork.
    /// </summary>
    /// <param name="mode">Which trigger is asking.</param>
    /// <param name="createdThisRun">
    /// Whether this collection was created by the run that is asking. Only the scheduled run
    /// ever creates collections, and it touches artwork on those and on nothing else — a
    /// collection that already existed keeps whatever poster it has until the user changes
    /// it, which the <c>ItemUpdated</c> listener picks up, or until the reapply action is
    /// asked for by hand.
    /// </param>
    /// <returns>The action to take.</returns>
    public static ArtworkAction Decide(ArtworkMode mode, bool createdThisRun) => mode switch
    {
        ArtworkMode.NewCollections => createdThisRun ? ArtworkAction.Apply : ArtworkAction.None,
        ArtworkMode.ReapplyFromFolder => ArtworkAction.Reapply,

        // A collection created a moment ago carries no poster, so there is nothing to adopt;
        // the listener never sees one either, since it only ever runs off an ItemUpdated for
        // an item already in ManagedCollections.
        ArtworkMode.AdoptOnly => createdThisRun ? ArtworkAction.None : ArtworkAction.Adopt,
        _ => ArtworkAction.None
    };
}
