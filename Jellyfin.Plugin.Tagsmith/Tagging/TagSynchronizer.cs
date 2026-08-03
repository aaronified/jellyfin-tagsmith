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

        // Shipped rules first, the user's second: Parse keeps the last rule written for a
        // value, so anything in the settings page overrides the default for that value.
        // Seeding PluginConfiguration.Aliases instead would only reach fresh installs —
        // an existing config already has the property saved, and its stored value wins
        // over any initialiser — so the defaults live here, where every install sees them.
        var aliases = TagAliasMap.Parse(
            TagAliasMap.DefaultRules(configuration.LanguageNamespace, configuration.AudioLanguageNamespace)
                .Concat(configuration.Aliases ?? []));

        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            foreach (var ns in provider.Namespaces(configuration))
            {
                // A blank namespace or separator must never become a prefix. The prefix is
                // a claim of ownership decided by StartsWith, and the degenerate claims are
                // catastrophic: "" owns every tag in the library, and a namespace with no
                // separator owns every tag that merely begins with the same letters.
                if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(configuration.Separator))
                {
                    continue;
                }

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

        // Ownership is by namespace, not by individual tag. Everything under a managed
        // prefix belongs to Tagsmith regardless of who typed it, so a value edited by hand
        // is regenerated on the next run like any other. Tags outside those prefixes are
        // never touched.
        var retained = item.Tags.Where(tag =>
            !configuration.RemoveObsoleteTags || !IsManaged(tag, managedPrefixes));

        var updated = retained
            .Concat(desired)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (updated.SequenceEqual(
                item.Tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase))
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
    /// <remarks>
    /// A dry run neither persists nor touches the live configuration object. It used to
    /// persist, which was the one thing a dry run wrote: a prefix is a claim of ownership
    /// over every tag carrying it, and recording that claim from a run whose whole purpose
    /// is to change nothing means an aborted experiment leaves Tagsmith deleting tags it
    /// was only ever asked to describe. Mutating the in-memory array without saving is the
    /// same bug on a delay — the configuration singleton outlives the run, and the next
    /// unrelated <c>SaveConfiguration()</c> would persist the dry-run claim with it.
    /// </remarks>
    private void RememberPrefixes(PluginConfiguration configuration, IEnumerable<string> activePrefixes)
    {
        var known = new HashSet<string>(configuration.KnownPrefixes, StringComparer.OrdinalIgnoreCase);
        var before = known.Count;
        known.UnionWith(activePrefixes);

        if (known.Count == before)
        {
            return;
        }

        var updated = known.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

        if (configuration.DryRun)
        {
            _logger.LogInformation(
                "[dry-run] Tagsmith would start managing prefixes: {Prefixes}",
                string.Join(", ", updated));
            return;
        }

        configuration.KnownPrefixes = updated;
        Plugin.Instance?.SaveConfiguration();
        _logger.LogInformation(
            "Tagsmith now manages prefixes: {Prefixes}",
            string.Join(", ", updated));
    }

    /// <summary>
    /// Whether a tag carries one of the managed prefixes. A blank prefix — which a
    /// hand-edited configuration file could still contain — matches nothing rather than
    /// everything; "" would otherwise claim, and delete, every tag in the library.
    /// </summary>
    private static bool IsManaged(string tag, HashSet<string> managedPrefixes) =>
        managedPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
