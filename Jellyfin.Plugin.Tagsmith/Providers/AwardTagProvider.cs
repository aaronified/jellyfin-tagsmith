using Jellyfin.Plugin.Tagsmith.Configuration;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Tagsmith.Providers;

/// <summary>
/// Tags award wins and nominations from the embedded dataset, keyed by IMDb id:
/// <c>award=oscar:best_picture</c>, <c>nomination=bafta:best_film</c>.
/// </summary>
/// <remarks>
/// <para>
/// No network: the dataset ships inside the plugin and is rebuilt at release time by
/// <c>scripts/generate-awards.mjs</c>. An item without an IMDb id — or one that simply
/// never won anything — gets no tags from here.
/// </para>
/// <para>
/// Values keep their two-part shape from the plan (<c>ceremony:category</c>), composed
/// directly rather than through <see cref="TagNormalizer.Compose"/>, which would fold the
/// colon into an underscore. Each segment was slugged by the generator, so the value is
/// already canonical.
/// </para>
/// <para>
/// Nominations deliberately include the eventual winner: "the Best Picture nominees"
/// should mean all of them. The winner-only view is what the award namespace is for.
/// </para>
/// </remarks>
public class AwardTagProvider : ITagProvider
{
    /// <inheritdoc />
    public string Name => "Awards";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Namespaces(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var namespaces = new List<string>();

        // A blank namespace is never claimed: the emitting path refuses to write to one,
        // and claiming what is never written prunes tags for nothing in return.
        if (configuration.EnableAwards && !string.IsNullOrWhiteSpace(configuration.AwardNamespace))
        {
            namespaces.Add(configuration.AwardNamespace);
        }

        if (configuration.EnableNominations && !string.IsNullOrWhiteSpace(configuration.NominationNamespace))
        {
            namespaces.Add(configuration.NominationNamespace);
        }

        return namespaces;
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

        if (!configuration.EnableAwards && !configuration.EnableNominations)
        {
            return Task.FromResult<IReadOnlyCollection<string>>(tags);
        }

        var record = CuratedData.AwardsFor(item.GetProviderId(MetadataProvider.Imdb));
        if (record is null)
        {
            return Task.FromResult<IReadOnlyCollection<string>>(tags);
        }

        // Null-tolerant: the XML file cannot produce a null array, but a raw configuration
        // POST can, and a broken setting must not abort the library pass.
        var ceremonies = new HashSet<string>(
            (configuration.AwardCeremonies ?? []).Select(TagNormalizer.Slug),
            StringComparer.Ordinal);

        if (configuration.EnableAwards)
        {
            AddValues(tags, record.Wins, ceremonies, configuration.AwardNamespace, configuration.Separator);
        }

        if (configuration.EnableNominations)
        {
            AddValues(tags, record.Nominations, ceremonies, configuration.NominationNamespace, configuration.Separator);
        }

        return Task.FromResult<IReadOnlyCollection<string>>(tags);
    }

    /// <summary>
    /// Composes tags for the values whose ceremony the user has enabled.
    /// </summary>
    private static void AddValues(
        HashSet<string> tags,
        IEnumerable<string> values,
        HashSet<string> ceremonies,
        string tagNamespace,
        string separator)
    {
        if (string.IsNullOrWhiteSpace(tagNamespace))
        {
            return;
        }

        foreach (var value in values)
        {
            // The ceremony is the first colon-separated segment: oscar:best_picture.
            var split = value.IndexOf(':', StringComparison.Ordinal);
            var ceremony = split > 0 ? value[..split] : value;

            if (ceremonies.Contains(ceremony))
            {
                tags.Add(string.Concat(tagNamespace, separator, value));
            }
        }
    }
}
