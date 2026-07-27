using Jellyfin.Plugin.Tagsmith.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// A source of tags for a media item. Register implementations in
/// <see cref="PluginServiceRegistrator"/>; all registered providers run for every item.
/// </summary>
public interface ITagProvider
{
    /// <summary>
    /// Gets the provider name, used in logs.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the tag namespaces this provider owns. Tags in these namespaces are treated as
    /// managed, so stale ones can be removed.
    /// </summary>
    IReadOnlyCollection<string> Namespaces(PluginConfiguration configuration);

    /// <summary>
    /// Produces the tags that should currently exist on <paramref name="item"/>.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetTagsAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken);
}
