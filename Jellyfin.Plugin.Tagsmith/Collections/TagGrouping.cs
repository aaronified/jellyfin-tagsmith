using System.Globalization;
using Jellyfin.Plugin.Tagsmith.Configuration;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Turns tags into the values a projection should produce collections for.
/// </summary>
/// <remarks>
/// Reads the tags actually on the item rather than what a provider would compute, so a
/// tag added by hand in the Jellyfin UI lands in its collection on the next run just like
/// a generated one.
/// </remarks>
public static class TagGrouping
{
    /// <summary>
    /// Returns the namespace configured for a projection.
    /// </summary>
    public static string NamespaceFor(ProjectionKind kind, PluginConfiguration configuration) => kind switch
    {
        ProjectionKind.Origin => configuration.OriginNamespace,
        ProjectionKind.Language => configuration.LanguageNamespace,
        ProjectionKind.Year => configuration.YearNamespace,
        _ => string.Empty
    };

    /// <summary>
    /// Returns whether a projection is switched on.
    /// </summary>
    public static bool IsEnabled(ProjectionKind kind, PluginConfiguration configuration) => kind switch
    {
        ProjectionKind.Origin => configuration.ProjectOrigin,
        ProjectionKind.Language => configuration.ProjectLanguage,
        ProjectionKind.Year => configuration.ProjectYear,
        _ => false
    };

    /// <summary>
    /// Returns the configured library name for a projection.
    /// </summary>
    public static string LibraryNameFor(ProjectionKind kind, PluginConfiguration configuration) => kind switch
    {
        ProjectionKind.Origin => configuration.OriginLibraryName,
        ProjectionKind.Language => configuration.LanguageLibraryName,
        ProjectionKind.Year => configuration.YearLibraryName,
        _ => string.Empty
    };

    /// <summary>
    /// Extracts the projected value from a tag, or null when the tag does not belong to
    /// this projection.
    /// </summary>
    /// <remarks>
    /// Year is the one case where the projection deliberately differs from the tag:
    /// <c>year=1954</c> stays per-year as a tag because that precision is what makes
    /// filtering useful, but it projects into a <c>1950s</c> collection so the library is
    /// ten tiles rather than a hundred.
    /// </remarks>
    public static string? ValueFor(ProjectionKind kind, string tag, string tagNamespace, string separator)
    {
        var prefix = tagNamespace + separator;
        if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = tag[prefix.Length..];
        if (value.Length == 0)
        {
            return null;
        }

        return kind == ProjectionKind.Year ? ToDecade(value) : value;
    }

    /// <summary>
    /// Converts a year to its decade label. Returns null for anything that is not a year.
    /// </summary>
    public static string? ToDecade(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || year < 1000
            || year > 9999)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{year / 10 * 10}s");
    }

    /// <summary>
    /// Turns a slug into the collection's display name: <c>united_states</c> becomes
    /// <c>United States</c>, while <c>1950s</c> is left alone.
    /// </summary>
    public static string DisplayName(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var words = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return value;
        }

        return string.Join(' ', words.Select(word =>
            char.IsDigit(word[0])
                ? word
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
