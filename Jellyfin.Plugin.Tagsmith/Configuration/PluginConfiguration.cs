using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Tagsmith.Configuration;

/// <summary>
/// Tagsmith settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the character placed between a namespace and its value.
    /// </summary>
    public string Separator { get; set; } = "=";

    /// <summary>
    /// Gets or sets a value indicating whether production-country tags are written.
    /// </summary>
    public bool EnableOrigin { get; set; } = true;

    /// <summary>
    /// Gets or sets the namespace used for production countries.
    /// </summary>
    public string OriginNamespace { get; set; } = "origin";

    /// <summary>
    /// Gets or sets a value indicating whether country names are resolved to a single
    /// canonical form via <see cref="Tagging.CountryAliasCatalog"/>.
    /// </summary>
    public bool CanonicaliseCountries { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether audio-language tags are written.
    /// </summary>
    public bool EnableLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets the namespace used for audio languages.
    /// </summary>
    public string LanguageNamespace { get; set; } = "lang";

    /// <summary>
    /// Gets or sets a value indicating whether release-year tags are written.
    /// </summary>
    public bool EnableYear { get; set; } = true;

    /// <summary>
    /// Gets or sets the namespace used for the first release year.
    /// </summary>
    public string YearNamespace { get; set; } = "year";

    /// <summary>
    /// Gets or sets a value indicating whether managed tags no longer produced by any
    /// provider are stripped from items.
    /// </summary>
    public bool RemoveObsoleteTags { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether changes are logged but not saved.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets rewrite rules applied to generated tags, one per line.
    /// See <see cref="Tagging.TagAliasMap"/> for the syntax.
    /// </summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether production countries are projected into a
    /// browsable collections library.
    /// </summary>
    public bool ProjectOrigin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether audio languages are projected.
    /// </summary>
    public bool ProjectLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether release years are projected, grouped by decade.
    /// </summary>
    public bool ProjectYear { get; set; }

    /// <summary>
    /// Gets or sets the library name for the origin projection.
    /// </summary>
    public string OriginLibraryName { get; set; } = "Origins";

    /// <summary>
    /// Gets or sets the library name for the language projection.
    /// </summary>
    public string LanguageLibraryName { get; set; } = "Languages";

    /// <summary>
    /// Gets or sets the library name for the year projection.
    /// </summary>
    public string YearLibraryName { get; set; } = "Decades";

    /// <summary>
    /// Gets or sets a value indicating whether disabling a projection also deletes the
    /// collections and library it created. Off by default: silently removing a browsing
    /// structure because a box was unticked is worse than leaving it behind.
    /// </summary>
    public bool RemoveCollectionsWhenDisabled { get; set; }

    /// <summary>
    /// Gets or sets the collections Tagsmith created, so it can modify and delete its own
    /// without ever touching one the user made by hand.
    /// </summary>
    public ManagedCollection[] ManagedCollections { get; set; } = [];

    /// <summary>
    /// Gets or sets the libraries Tagsmith created, keyed by projection.
    /// </summary>
    public ManagedLibrary[] ManagedLibraries { get; set; } = [];

    /// <summary>
    /// Gets or sets every tag prefix Tagsmith has ever written, e.g. <c>origin=</c>.
    /// </summary>
    /// <remarks>
    /// Maintained automatically. Pruning is prefix-based, so this list is what lets
    /// Tagsmith clean up after a namespace rename, a separator change, or a namespace
    /// being switched off — in all three cases the old prefix is no longer active but
    /// its tags still need removing. Delete an entry here to make Tagsmith forget (and
    /// therefore stop managing and stop deleting) tags with that prefix.
    /// </remarks>
    public string[] KnownPrefixes { get; set; } = [];
}
