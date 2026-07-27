using Jellyfin.Plugin.Tagsmith.Collections;
using Jellyfin.Plugin.Tagsmith.Providers;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Tagsmith;

/// <summary>
/// Registers Tagsmith services with the server's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Add further ITagProvider implementations here (TMDb, awards, curated lists).
        serviceCollection.AddSingleton<ITagProvider, CoreMetadataTagProvider>();
        serviceCollection.AddSingleton<TagSynchronizer>();
        serviceCollection.AddSingleton<CollectionProjector>();
    }
}
