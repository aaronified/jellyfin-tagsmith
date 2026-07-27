using System.Globalization;
using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Tagsmith.Providers;

/// <summary>
/// Derives tags from metadata Jellyfin already holds: production countries, audio
/// languages and the first release year. No network calls.
/// </summary>
public class CoreMetadataTagProvider : ITagProvider
{
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoreMetadataTagProvider"/> class.
    /// </summary>
    public CoreMetadataTagProvider(
        IMediaSourceManager mediaSourceManager,
        ILocalizationManager localization)
    {
        _mediaSourceManager = mediaSourceManager;
        _localization = localization;
    }

    /// <inheritdoc />
    public string Name => "Core metadata";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Namespaces(PluginConfiguration configuration)
    {
        var namespaces = new List<string>();

        if (configuration.EnableOrigin)
        {
            namespaces.Add(configuration.OriginNamespace);
        }

        if (configuration.EnableLanguage)
        {
            namespaces.Add(configuration.LanguageNamespace);
        }

        if (configuration.EnableYear)
        {
            namespaces.Add(configuration.YearNamespace);
        }

        return namespaces;
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<string>> GetTagsAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (configuration.EnableOrigin)
        {
            foreach (var country in item.ProductionLocations)
            {
                // Canonicalise first, so the same country spelled differently across
                // metadata sources and languages collapses to one tag.
                var canonical = configuration.CanonicaliseCountries
                    ? CountryAliasCatalog.Resolve(country)
                    : country;

                Add(tags, configuration.OriginNamespace, configuration.Separator, canonical);
            }
        }

        if (configuration.EnableYear && item.ProductionYear is > 0)
        {
            Add(
                tags,
                configuration.YearNamespace,
                configuration.Separator,
                item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (configuration.EnableLanguage)
        {
            foreach (var language in GetAudioLanguages(item))
            {
                Add(tags, configuration.LanguageNamespace, configuration.Separator, language);
            }
        }

        return Task.FromResult<IReadOnlyCollection<string>>(tags);
    }

    private static void Add(HashSet<string> tags, string tagNamespace, string separator, string? value)
    {
        var tag = TagNormalizer.Compose(tagNamespace, separator, value);
        if (tag is not null)
        {
            tags.Add(tag);
        }
    }

    /// <summary>
    /// Resolves audio stream language codes to English display names, so
    /// <c>ben</c> becomes <c>lang=bengali</c> rather than an opaque code.
    /// </summary>
    private IEnumerable<string> GetAudioLanguages(BaseItem item)
    {
        var streams = _mediaSourceManager.GetMediaStreams(item.Id);

        foreach (var stream in streams)
        {
            if (stream.Type != MediaStreamType.Audio || string.IsNullOrWhiteSpace(stream.Language))
            {
                continue;
            }

            var culture = _localization.FindLanguageInfo(stream.Language);
            yield return culture?.DisplayName ?? stream.Language;
        }
    }
}
