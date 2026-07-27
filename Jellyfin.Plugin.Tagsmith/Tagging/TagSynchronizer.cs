using Jellyfin.Plugin.Tagsmith.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// Applies provider output to items: adds new managed tags, drops stale ones and leaves
/// tags outside the managed namespaces untouched.
/// </summary>
public class TagSynchronizer
{
    private readonly ILibraryManager _libraryManager;
    private readonly IEnumerable<ITagProvider> _providers;
    private readonly ILogger<TagSynchronizer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagSynchronizer"/> class.
    /// </summary>
    public TagSynchronizer(
        ILibraryManager libraryManager,
        IEnumerable<ITagProvider> providers,
        ILogger<TagSynchronizer> logger)
    {
        _libraryManager = libraryManager;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Recomputes tags for a single item. Returns true when the item was changed.
    /// </summary>
    public async Task<bool> SyncAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            foreach (var ns in provider.Namespaces(configuration))
            {
                managedPrefixes.Add(ns + configuration.Separator);
            }

            foreach (var tag in await provider.GetTagsAsync(item, configuration, cancellationToken)
                         .ConfigureAwait(false))
            {
                desired.Add(tag);
            }
        }

        var retained = item.Tags.Where(tag =>
            !configuration.RemoveObsoleteTags || !IsManaged(tag, managedPrefixes));

        var updated = retained
            .Concat(desired)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (updated.SequenceEqual(item.Tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (configuration.DryRun)
        {
            _logger.LogInformation(
                "[dry-run] {Item} tags would become: {Tags}",
                item.Name,
                string.Join(", ", updated));
            return false;
        }

        item.Tags = updated;

        await _libraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Updated tags on {Item}: {Tags}", item.Name, string.Join(", ", updated));
        return true;
    }

    private static bool IsManaged(string tag, HashSet<string> managedPrefixes) =>
        managedPrefixes.Any(prefix => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
