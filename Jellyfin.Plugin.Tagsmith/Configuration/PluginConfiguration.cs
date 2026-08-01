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
    /// Gets or sets a value indicating whether language tags are written. The value is the
    /// item's <em>original</em> language — the language the film or show was made in — from
    /// TMDb/TVDb when external metadata is on, falling back to the audio streams on the
    /// files when no external source knows the item.
    /// </summary>
    public bool EnableLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets the namespace used for the original language.
    /// </summary>
    public string LanguageNamespace { get; set; } = "lang";

    /// <summary>
    /// Gets or sets a value indicating whether audio-track language tags are written —
    /// one per language actually present on the media files. Off by default; the original
    /// language above is what most libraries want to browse by.
    /// </summary>
    public bool EnableAudioLanguage { get; set; }

    /// <summary>
    /// Gets or sets the namespace used for audio-track languages.
    /// </summary>
    public string AudioLanguageNamespace { get; set; } = "audio_lang";

    /// <summary>
    /// Gets or sets a value indicating whether origin and language are looked up in TMDb
    /// and TVDb first, with Jellyfin's own metadata as the fallback.
    /// </summary>
    /// <remarks>
    /// TMDb is reached through the server's built-in client. TVDb needs the official
    /// TheTVDB plugin installed; without it, series matched only by a TVDb id are bridged
    /// through TMDb where possible and otherwise fall back to Jellyfin metadata.
    /// </remarks>
    public bool UseExternalMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether release-year tags are written.
    /// </summary>
    public bool EnableYear { get; set; } = true;

    /// <summary>
    /// Gets or sets the namespace used for the first release year.
    /// </summary>
    public string YearNamespace { get; set; } = "year";

    /// <summary>
    /// Gets or sets a value indicating whether award-winner tags are written, e.g.
    /// <c>award=oscar:best_picture</c>. Off by default.
    /// </summary>
    public bool EnableAwards { get; set; }

    /// <summary>
    /// Gets or sets the namespace used for award wins.
    /// </summary>
    public string AwardNamespace { get; set; } = "award";

    /// <summary>
    /// Gets or sets a value indicating whether nomination tags are written, e.g.
    /// <c>nomination=bafta:best_film</c>. Nominations include the eventual winner, so the
    /// nominee set for a category is complete on its own. Off by default.
    /// </summary>
    public bool EnableNominations { get; set; }

    /// <summary>
    /// Gets or sets the namespace used for award nominations.
    /// </summary>
    public string NominationNamespace { get; set; } = "nomination";

    /// <summary>
    /// Gets or sets which ceremonies produce award and nomination tags. Values are the
    /// ceremony slugs used in tag values: <c>oscar</c>, <c>bafta</c>, <c>golden_globe</c>,
    /// <c>emmy</c>.
    /// </summary>
    public string[] AwardCeremonies { get; set; } = ["oscar", "bafta", "golden_globe", "emmy"];

    /// <summary>
    /// Gets or sets which curated lists produce <c>list=</c> tags, by list slug (e.g.
    /// <c>imdb_top_250</c>, <c>criterion_collection</c>). Empty — the default — turns the
    /// namespace off.
    /// </summary>
    public string[] EnabledLists { get; set; } = [];

    /// <summary>
    /// Gets or sets the namespace used for curated-list membership.
    /// </summary>
    public string ListNamespace { get; set; } = "list";

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
    /// Gets or sets a value indicating whether languages are projected.
    /// </summary>
    public bool ProjectLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether release years are projected, grouped by decade.
    /// </summary>
    public bool ProjectYear { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether award wins are projected, one collection per
    /// ceremony and category — "Oscar – Best Picture".
    /// </summary>
    /// <remarks>
    /// Only the ceremonies selected in <see cref="AwardCeremonies"/> produce tags, so they
    /// are the only ones that produce collections. Around 80 categories exist across all
    /// four ceremonies; the first run that builds them is a long one.
    /// </remarks>
    public bool ProjectAward { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether award nominations are projected, one
    /// collection per ceremony and category.
    /// </summary>
    /// <remarks>
    /// Nominations include the eventual winner, so these collections are supersets of the
    /// matching <see cref="ProjectAward"/> ones rather than an alternative to them.
    /// </remarks>
    public bool ProjectNomination { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether curated-list membership is projected, one
    /// collection per list in <see cref="EnabledLists"/>.
    /// </summary>
    public bool ProjectList { get; set; }

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
    /// Gets or sets the library name for the award projection.
    /// </summary>
    public string AwardLibraryName { get; set; } = "Awards";

    /// <summary>
    /// Gets or sets the library name for the nomination projection.
    /// </summary>
    public string NominationLibraryName { get; set; } = "Nominations";

    /// <summary>
    /// Gets or sets the library name for the curated-list projection.
    /// </summary>
    public string ListLibraryName { get; set; } = "Curated Lists";

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
