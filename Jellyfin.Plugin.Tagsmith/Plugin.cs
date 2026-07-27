using Jellyfin.Plugin.Tagsmith.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Tagsmith;

/// <summary>
/// Tagsmith plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Tagsmith";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4f2a9c31-6d5b-4c8e-9a70-1b3e5d7f2c84");

    /// <inheritdoc />
    public override string Description =>
        "Automatically derives namespaced, searchable tags (origin=, lang=, year= ...) from media metadata.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            DisplayName = "Tagsmith",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "local_offer",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    ];
}
