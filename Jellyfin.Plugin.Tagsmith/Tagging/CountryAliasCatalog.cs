using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// Maps any spelling of a country — English, endonym, any CLDR locale, ISO alpha-2 or
/// alpha-3, plus curated official long names — onto one canonical English name slug, so
/// <c>United States of America</c>, <c>USA</c>, <c>Estados Unidos</c> and <c>美国</c> all
/// tag as <c>origin=united_states</c>.
/// </summary>
/// <remarks>
/// Backed by a gzipped resource generated from Unicode CLDR by
/// <c>scripts/generate-countries.mjs</c>. Values that are not ISO 3166-1 territories —
/// Soviet Union, Yugoslavia, West Germany — deliberately have no entry and pass through
/// with their own slug rather than being folded into a successor state.
/// </remarks>
public static class CountryAliasCatalog
{
    private const string ResourceName = "Jellyfin.Plugin.Tagsmith.Data.countries.json.gz";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _map = new(Load);

    /// <summary>
    /// Gets the number of known aliases.
    /// </summary>
    public static int Count => _map.Value.Count;

    /// <summary>
    /// Returns the canonical slug for a country name, or the normalised input when the
    /// value is not a recognised territory.
    /// </summary>
    public static string Resolve(string? value)
    {
        var slug = TagNormalizer.Slug(value);
        if (slug.Length == 0)
        {
            return string.Empty;
        }

        return _map.Value.TryGetValue(slug, out var canonical) ? canonical : slug;
    }

    /// <summary>
    /// Returns true when the value maps to a known ISO 3166-1 territory.
    /// </summary>
    public static bool IsKnown(string? value) =>
        _map.Value.ContainsKey(TagNormalizer.Slug(value));

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(CountryAliasCatalog).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource missing: {ResourceName}");

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(gzip)
            ?? new Dictionary<string, string>();
    }
}
