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

        // Subscribes to ILibraryManager.ItemUpdated on start and unsubscribes on stop, so a
        // poster set on one of Tagsmith's collections is backed up into the thumbnails folder
        // straight away rather than waiting for the nightly pass. The listener shares the
        // singleton projector, which is what makes its loop guard work.
        serviceCollection.AddHostedService<PosterAdoptionListener>();
    }
}
