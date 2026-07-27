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
    /// Gets or sets a value indicating whether every item is reprocessed on the next run.
    /// </summary>
    public bool ForceFullRescan { get; set; }
}
