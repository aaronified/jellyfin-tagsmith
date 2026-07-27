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
        var aliases = TagAliasMap.Parse(configuration.Aliases);

        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            foreach (var ns in provider.Namespaces(configuration))
            {
                activePrefixes.Add(ns + configuration.Separator);
            }

            foreach (var tag in await provider.GetTagsAsync(item, configuration, cancellationToken)
                         .ConfigureAwait(false))
            {
                var mapped = aliases.Apply(tag, configuration.Separator);
                if (mapped is not null)
                {
                    desired.Add(mapped);
                }
            }
        }

        RememberPrefixes(configuration, activePrefixes);

        // Prune against every prefix ever written, not just the active ones, so renaming
        // a namespace or disabling one still cleans up the tags it left behind.
        var managedPrefixes = new HashSet<string>(configuration.KnownPrefixes, StringComparer.OrdinalIgnoreCase);
        managedPrefixes.UnionWith(activePrefixes);

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

    /// <summary>
    /// Records any newly seen prefix in configuration so it stays prunable after the
    /// namespace or separator that produced it changes.
    /// </summary>
    private void RememberPrefixes(PluginConfiguration configuration, IEnumerable<string> activePrefixes)
    {
        var known = new HashSet<string>(configuration.KnownPrefixes, StringComparer.OrdinalIgnoreCase);
        var before = known.Count;
        known.UnionWith(activePrefixes);

        if (known.Count == before)
        {
            return;
        }

        configuration.KnownPrefixes = known.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Plugin.Instance?.SaveConfiguration();
        _logger.LogInformation(
            "Tagsmith now manages prefixes: {Prefixes}",
            string.Join(", ", configuration.KnownPrefixes));
    }

    private static bool IsManaged(string tag, HashSet<string> managedPrefixes) =>
        managedPrefixes.Any(prefix => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
