namespace Jellyfin.Plugin.Tagsmith.Configuration;

/// <summary>
/// The three things Tagsmith can project into a browsable library.
/// </summary>
/// <remarks>
/// Projections are keyed by kind rather than by the user's namespace string, so renaming
/// a namespace from <c>origin</c> to <c>country</c> keeps pointing at the same library
/// instead of orphaning it and building a second one.
/// </remarks>
public enum ProjectionKind
{
    /// <summary>Production countries.</summary>
    Origin,

    /// <summary>Languages, from the language namespace's tags.</summary>
    Language,

    /// <summary>Release years, grouped by decade.</summary>
    Year
}

/// <summary>
/// The artwork bookkeeping shared by everything Tagsmith can put a poster on — projected
/// collections and the libraries that hold them. Two hashes per record, never one; see
/// <see cref="ManagedCollection.AppliedImageHash"/> for why they differ.
/// </summary>
public interface IArtworkRecord
{
    /// <summary>
    /// Gets or sets a hash of the artwork file in the thumbnails folder that Tagsmith last
    /// applied, so a run with nothing changed writes nothing.
    /// </summary>
    string ImageHash { get; set; }

    /// <summary>
    /// Gets or sets a hash of the image file Tagsmith's own <c>SaveImage</c> call produced
    /// on the item.
    /// </summary>
    string AppliedImageHash { get; set; }
}

/// <summary>
/// A collection Tagsmith created and therefore owns.
/// </summary>
public class ManagedCollection : IArtworkRecord
{
    /// <summary>Gets or sets the projection this collection belongs to.</summary>
    public ProjectionKind Kind { get; set; }

    /// <summary>Gets or sets the tag value, e.g. <c>india</c> or <c>1950s</c>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the box set id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the box set folder on disk.
    /// </summary>
    /// <remarks>
    /// Recorded so the folder can still be cleaned up after the library it lived in was
    /// removed out of band and its database items went with it. Without this a box set
    /// orphaned that way survives unowned and unreachable forever.
    /// </remarks>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a hash of the artwork file in the thumbnails folder that Tagsmith last
    /// applied, so a run with nothing changed writes nothing.
    /// </summary>
    public string ImageHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a hash of the image file Tagsmith's own <c>SaveImage</c> call produced on
    /// the collection.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ImageHash"/> because the server re-encodes on save, so the
    /// bytes on the item are not the bytes in the thumbnails folder. Comparing against this
    /// is how a poster the user set by hand is told apart from the one Tagsmith put there —
    /// without it, Tagsmith's own artwork reads as user intent and gets copied back over the
    /// user's curated file.
    /// </remarks>
    public string AppliedImageHash { get; set; } = string.Empty;
}

/// <summary>
/// A library Tagsmith created and therefore owns.
/// </summary>
/// <remarks>
/// Ownership is recorded by <see cref="ItemId"/>. A library is only ever modified or removed
/// when its id matches; a library that merely shares a name is left alone. See
/// <see cref="Collections.LibraryOwnership"/>.
/// </remarks>
public class ManagedLibrary : IArtworkRecord
{
    /// <summary>Gets or sets the projection this library serves.</summary>
    public ProjectionKind Kind { get; set; }

    /// <summary>
    /// Gets or sets a hash of the library-tile artwork file
    /// (<c>thumbnails/&lt;namespace&gt;.png</c>) Tagsmith last applied.
    /// </summary>
    public string ImageHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a hash of the image Tagsmith's own <c>SaveImage</c> call produced on the
    /// library's <c>CollectionFolder</c>. Same two-hash rule as
    /// <see cref="ManagedCollection.AppliedImageHash"/>: the server re-encodes on save, so
    /// the bytes on the item are not the bytes in the thumbnails folder.
    /// </summary>
    public string AppliedImageHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name as Jellyfin last reported it.
    /// </summary>
    /// <remarks>
    /// Refreshed on every run, because the user can rename a library in the dashboard and
    /// <c>ILibraryManager.RemoveVirtualFolder</c> takes a name, not an id. Never used to
    /// decide ownership.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sanitised library name from Tagsmith's settings at the time the
    /// library was created, so a change to that setting can be told apart from a rename made
    /// in Jellyfin's dashboard.
    /// </summary>
    public string ConfiguredName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the <c>CollectionFolder</c> id. This is the ownership token.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;
}
