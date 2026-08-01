using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tagsmith.Providers;

/// <summary>
/// Tags membership of curated lists — <c>list=imdb_top_250</c>,
/// <c>list=criterion_collection</c> — from the embedded dataset, keyed by IMDb id.
/// </summary>
/// <remarks>
/// <para>
/// No network: the dataset ships inside the plugin and is rebuilt at release time by
/// <c>scripts/generate-lists.mjs</c>. The lists are snapshots — the IMDb Top 250 as of the
/// release, not as of tonight — which is the honest thing a tag can be.
/// </para>
/// <para>
/// The namespace is active only while at least one list is selected, so deselecting them
/// all both stops producing tags and (through the usual prefix pruning) cleans up the ones
/// already written.
/// </para>
/// </remarks>
public class ListTagProvider : ITagProvider
{
    /// <inheritdoc />
    public string Name => "Curated lists";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Namespaces(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // An empty dataset claims nothing — see AwardTagProvider.Namespaces for why that
        // matters more than it looks. Null-tolerant too (a raw configuration POST can null
        // the array), and a blank namespace is never claimed.
        return CuratedData.ListTitleCount > 0
               && configuration.EnabledLists is { Length: > 0 }
               && !string.IsNullOrWhiteSpace(configuration.ListNamespace)
            ? [configuration.ListNamespace]
            : [];
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<string>> GetTagsAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(configuration);

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (configuration.EnabledLists is not { Length: > 0 } || string.IsNullOrWhiteSpace(configuration.ListNamespace))
        {
            return Task.FromResult<IReadOnlyCollection<string>>(tags);
        }

        var enabled = new HashSet<string>(
            configuration.EnabledLists.Select(TagNormalizer.Slug),
            StringComparer.Ordinal);

        foreach (var list in CuratedData.ListsFor(item.GetProviderId(MetadataProvider.Imdb)))
        {
            if (enabled.Contains(list))
            {
                tags.Add(string.Concat(configuration.ListNamespace, configuration.Separator, list));
            }
        }

        return Task.FromResult<IReadOnlyCollection<string>>(tags);
    }
}
