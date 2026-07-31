using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.External;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Providers;

/// <summary>
/// Derives the core tag set — origin, original language, audio languages and release year.
/// </summary>
/// <remarks>
/// Origin and original language are asked of the external sources first (TMDb through the
/// server's built-in client, TVDb through the official plugin when installed), because the
/// databases know what a film <em>is</em> — a Bengali film keeps <c>lang=bengali</c> even
/// when the file on disk carries only a Hindi dub, and production countries stop depending
/// on which metadata provider happened to fill the item. Jellyfin's own metadata is the
/// fallback for items no external source knows, and the whole external layer switches off
/// with one setting.
/// </remarks>
public class CoreMetadataTagProvider : ITagProvider
{
    /// <summary>
    /// How many episodes to sample when a series falls back to audio streams. Streams cost
    /// one query per episode, and a language present in none of the first few dozen
    /// episodes is unlikely to define the series.
    /// </summary>
    private const int EpisodeSampleSize = 32;

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILocalizationManager _localization;
    private readonly ILibraryManager _libraryManager;
    private readonly IReadOnlyList<IExternalMetadataSource> _external;
    private readonly ILogger<CoreMetadataTagProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoreMetadataTagProvider"/> class.
    /// </summary>
    public CoreMetadataTagProvider(
        IMediaSourceManager mediaSourceManager,
        ILocalizationManager localization,
        ILibraryManager libraryManager,
        IEnumerable<IExternalMetadataSource> externalSources,
        ILogger<CoreMetadataTagProvider> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _localization = localization;
        _libraryManager = libraryManager;

        // Registration order is priority order: TMDb answers first, TVDb fills gaps.
        _external = externalSources.ToArray();
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Core metadata";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Namespaces(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var namespaces = new List<string>();

        if (configuration.EnableOrigin)
        {
            namespaces.Add(configuration.OriginNamespace);
        }

        if (configuration.EnableLanguage)
        {
            namespaces.Add(configuration.LanguageNamespace);
        }

        if (configuration.EnableAudioLanguage)
        {
            namespaces.Add(configuration.AudioLanguageNamespace);
        }

        if (configuration.EnableYear)
        {
            namespaces.Add(configuration.YearNamespace);
        }

        return namespaces;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GetTagsAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(configuration);

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One external lookup serves both origin and language; skipped entirely when
        // neither namespace wants it or the user turned the external layer off.
        var (external, lookupFailed) = configuration.UseExternalMetadata && (configuration.EnableOrigin || configuration.EnableLanguage)
            ? await LookupExternalAsync(item, cancellationToken).ConfigureAwait(false)
            : (null, false);

        if (configuration.EnableOrigin)
        {
            AddOriginTags(tags, item, external, lookupFailed, configuration);
        }

        if (configuration.EnableLanguage)
        {
            AddLanguageTags(tags, item, external, lookupFailed, configuration);
        }

        if (configuration.EnableAudioLanguage)
        {
            AddAudioLanguageTags(tags, item, configuration);
        }

        if (configuration.EnableYear && item.ProductionYear is > 0)
        {
            Add(
                tags,
                configuration.YearNamespace,
                configuration.Separator,
                item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
        }

        return tags;
    }

    // ---------------------------------------------------------------- origin

    /// <summary>
    /// Tags every production country: the external answer when there is one, otherwise
    /// whatever Jellyfin's metadata holds.
    /// </summary>
    private static void AddOriginTags(
        HashSet<string> tags,
        BaseItem item,
        ExternalItemInfo? external,
        bool lookupFailed,
        PluginConfiguration configuration)
    {
        // A failed lookup is not evidence about the item. Falling back would rewrite
        // correct external tags to the fallback values on every transient outage, so the
        // item's existing origin tags are re-emitted unchanged instead.
        if (lookupFailed && external?.Countries is not { Count: > 0 })
        {
            PreserveExisting(tags, item, configuration.OriginNamespace, configuration.Separator);
            return;
        }

        var countries = external?.Countries is { Count: > 0 } fromExternal
            ? fromExternal
            : item.ProductionLocations;

        foreach (var country in countries)
        {
            // Canonicalise first, so the same country spelled differently across metadata
            // sources and languages — including bare ISO codes from TMDb and TVDb —
            // collapses to one tag.
            var canonical = configuration.CanonicaliseCountries
                ? CountryAliasCatalog.Resolve(country)
                : country;

            Add(tags, configuration.OriginNamespace, configuration.Separator, canonical);
        }
    }

    // ---------------------------------------------------------------- language

    /// <summary>
    /// Tags the item's original language. Falls back to the audio-stream languages when no
    /// external source knows the item — Jellyfin itself does not record original language.
    /// </summary>
    private void AddLanguageTags(
        HashSet<string> tags,
        BaseItem item,
        ExternalItemInfo? external,
        bool lookupFailed,
        PluginConfiguration configuration)
    {
        var original = DisplayLanguage(external?.OriginalLanguage);
        if (original is not null)
        {
            Add(tags, configuration.LanguageNamespace, configuration.Separator, original);
            return;
        }

        // Same rule as origin: an outage keeps the item's existing language tags rather
        // than rewriting them from the audio streams.
        if (lookupFailed)
        {
            PreserveExisting(tags, item, configuration.LanguageNamespace, configuration.Separator);
            return;
        }

        foreach (var language in GetAudioLanguages(item))
        {
            Add(tags, configuration.LanguageNamespace, configuration.Separator, language);
        }
    }

    /// <summary>
    /// Re-emits the item's existing tags for one namespace, making the sync a no-op for
    /// that namespace on that item. Used when an external outage leaves Tagsmith without
    /// evidence either way.
    /// </summary>
    private static void PreserveExisting(HashSet<string> tags, BaseItem item, string tagNamespace, string separator)
    {
        var prefix = tagNamespace + separator;

        foreach (var tag in item.Tags)
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }
    }

    /// <summary>
    /// Tags every language spoken on the media files' audio tracks.
    /// </summary>
    private void AddAudioLanguageTags(HashSet<string> tags, BaseItem item, PluginConfiguration configuration)
    {
        foreach (var language in GetAudioLanguages(item))
        {
            Add(tags, configuration.AudioLanguageNamespace, configuration.Separator, language);
        }
    }

    /// <summary>
    /// Resolves a language code to its English display name, so <c>bn</c> and <c>ben</c>
    /// both tag as <c>bengali</c> rather than as two different opaque codes.
    /// </summary>
    private string? DisplayLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || LanguageCodes.IsNoLanguage(code))
        {
            return null;
        }

        if (LanguageCodes.DisplayOverride(code) is { } overridden)
        {
            return overridden;
        }

        var normalised = LanguageCodes.Normalise(code);
        return _localization.FindLanguageInfo(normalised)?.DisplayName ?? normalised;
    }

    // ---------------------------------------------------------------- audio streams

    /// <summary>
    /// The audio-track languages actually on the files, resolved to display names.
    /// </summary>
    /// <remarks>
    /// A series has no streams of its own — they live on the episodes — so it samples the
    /// first <see cref="EpisodeSampleSize"/> episodes instead. This is what used to make
    /// language tags silently come out empty for every series.
    /// </remarks>
    private IEnumerable<string> GetAudioLanguages(BaseItem item)
    {
        var carriers = item is Series series
            ? _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,

                // Chronological, so "the first episodes" means what it says. The query's
                // default order is SortName, which would sample alphabetically by title.
                OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Ascending)],
                Limit = EpisodeSampleSize
            })
            : [item];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var carrier in carriers)
        {
            foreach (var stream in _mediaSourceManager.GetMediaStreams(carrier.Id))
            {
                if (stream.Type != MediaStreamType.Audio || string.IsNullOrWhiteSpace(stream.Language))
                {
                    continue;
                }

                if (seen.Add(stream.Language) && DisplayLanguage(stream.Language) is { } name)
                {
                    yield return name;
                }
            }
        }
    }

    // ---------------------------------------------------------------- external

    /// <summary>
    /// Asks each external source in priority order and merges the answers field by field,
    /// stopping as soon as both fields are filled.
    /// </summary>
    /// <returns>
    /// The merged answer, and whether any source <em>failed</em> rather than answered.
    /// The flag is what lets the caller tell "no source knows this item" (fall back to
    /// Jellyfin metadata) from "the lookup broke" (keep the item's existing tags).
    /// </returns>
    private async Task<(ExternalItemInfo? Info, bool Failed)> LookupExternalAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ExternalItemInfo? merged = null;
        var failed = false;

        foreach (var source in _external)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExternalItemInfo? info;
            try
            {
                info = await source.GetAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalLookupException ex)
            {
                // The source already warned once at default log level.
                _logger.LogDebug(ex, "Tagsmith: the {Source} lookup failed for {Item}", source.Name, item.Name);
                failed = true;
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Sources fail soft themselves; this is the belt to that braces. A broken
                // source must degrade to "use the next one", never break the tag pass.
                _logger.LogDebug(ex, "Tagsmith: the {Source} lookup failed for {Item}", source.Name, item.Name);
                failed = true;
                continue;
            }

            if (info is null || info.IsEmpty)
            {
                continue;
            }

            merged = ExternalItemInfo.Merge(merged, info);
            if (merged.IsComplete)
            {
                break;
            }
        }

        return (merged, failed);
    }

    private static void Add(HashSet<string> tags, string tagNamespace, string separator, string? value)
    {
        var tag = TagNormalizer.Compose(tagNamespace, separator, value);
        if (tag is not null)
        {
            tags.Add(tag);
        }
    }
}
