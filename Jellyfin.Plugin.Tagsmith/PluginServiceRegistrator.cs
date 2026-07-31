using Jellyfin.Plugin.Tagsmith.Collections;
using Jellyfin.Plugin.Tagsmith.External;
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
        // External databases, consulted in registration order: TMDb answers first, TVDb
        // fills the gaps. Both reach the respective client by reflection and fail soft to
        // "unavailable" when the server or plugin surface moves.
        serviceCollection.AddSingleton<IExternalMetadataSource, TmdbMetadataSource>();
        serviceCollection.AddSingleton<IExternalMetadataSource, TvdbMetadataSource>();

        // Add further ITagProvider implementations here; all registered providers run for
        // every item and their tags are unioned.
        serviceCollection.AddSingleton<ITagProvider, CoreMetadataTagProvider>();
        serviceCollection.AddSingleton<TagSynchronizer>();
        serviceCollection.AddSingleton<ArtworkSynchronizer>();
        serviceCollection.AddSingleton<CollectionProjector>();

        // Subscribes to ILibraryManager.ItemUpdated on start and unsubscribes on stop, so a
        // poster set on one of Tagsmith's collections or libraries is backed up into the
        // thumbnails folder straight away rather than waiting for the nightly pass. The
        // listener shares the singleton artwork synchroniser, which is what makes its loop
        // guard work.
        serviceCollection.AddHostedService<PosterAdoptionListener>();
    }
}
