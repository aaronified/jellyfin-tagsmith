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

    /// <summary>Audio languages.</summary>
    Language,

    /// <summary>Release years, grouped by decade.</summary>
    Year
}

/// <summary>
/// A collection Tagsmith created and therefore owns.
/// </summary>
public class ManagedCollection
{
    /// <summary>Gets or sets the projection this collection belongs to.</summary>
    public ProjectionKind Kind { get; set; }

    /// <summary>Gets or sets the tag value, e.g. <c>india</c> or <c>1950s</c>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the box set id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a hash of the image file Tagsmith last applied, or empty if it applied
    /// none. Used to leave a hand-picked poster alone while still following changes to the
    /// source file.
    /// </summary>
    public string ImageHash { get; set; } = string.Empty;
}

/// <summary>
/// A library Tagsmith created and therefore owns.
/// </summary>
public class ManagedLibrary
{
    /// <summary>Gets or sets the projection this library serves.</summary>
    public ProjectionKind Kind { get; set; }

    /// <summary>Gets or sets the library name as registered with Jellyfin.</summary>
    public string Name { get; set; } = string.Empty;
}
