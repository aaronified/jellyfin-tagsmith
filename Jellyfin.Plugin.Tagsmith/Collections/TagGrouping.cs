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
        ProjectionKind.Award => configuration.AwardNamespace,
        ProjectionKind.Nomination => configuration.NominationNamespace,
        ProjectionKind.List => configuration.ListNamespace,
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
        ProjectionKind.Award => configuration.ProjectAward,
        ProjectionKind.Nomination => configuration.ProjectNomination,
        ProjectionKind.List => configuration.ProjectList,
        _ => false
    };

    /// <summary>
    /// Switches a projection off — the inverse of <see cref="IsEnabled"/>, and kept beside it
    /// so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// Used when a library is found to have been deleted in Jellyfin's own settings, which
    /// Tagsmith treats as intent rather than as something to undo. A kind missing from this
    /// switch would leave its projection ticked, and the next run would recreate the library
    /// the user deliberately removed — the exact failure the ownership rules exist to
    /// prevent, and one no test could reach while this was a private method on the projector.
    /// </remarks>
    public static void SetEnabled(ProjectionKind kind, PluginConfiguration configuration, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        switch (kind)
        {
            case ProjectionKind.Origin:
                configuration.ProjectOrigin = enabled;
                break;
            case ProjectionKind.Language:
                configuration.ProjectLanguage = enabled;
                break;
            case ProjectionKind.Year:
                configuration.ProjectYear = enabled;
                break;
            case ProjectionKind.Award:
                configuration.ProjectAward = enabled;
                break;
            case ProjectionKind.Nomination:
                configuration.ProjectNomination = enabled;
                break;
            case ProjectionKind.List:
                configuration.ProjectList = enabled;
                break;
        }
    }

    /// <summary>
    /// Whether anything is currently writing tags into the namespace a projection reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projection is not the same switch as the tagging it projects, and for the three
    /// newest namespaces the tagging defaults <em>off</em>: ticking "project award wins"
    /// without ticking the awards themselves would build a library, keep it alive, and put
    /// nothing in it — a permanently empty shelf on the home screen with nothing in the log
    /// to explain it.
    /// </para>
    /// <para>
    /// Advisory only. Ownership is by namespace, so a tag added by hand under a namespace
    /// whose provider is switched off is still projected; this answers "should anyone be
    /// surprised that there is nothing here", not "should this projection run".
    /// </para>
    /// </remarks>
    public static bool SourceIsTagged(ProjectionKind kind, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return kind switch
        {
            ProjectionKind.Origin => configuration.EnableOrigin,
            ProjectionKind.Language => configuration.EnableLanguage,
            ProjectionKind.Year => configuration.EnableYear,
            ProjectionKind.Award => configuration.EnableAwards && configuration.AwardCeremonies is { Length: > 0 },
            ProjectionKind.Nomination => configuration.EnableNominations && configuration.AwardCeremonies is { Length: > 0 },
            ProjectionKind.List => configuration.EnabledLists is { Length: > 0 },
            _ => false
        };
    }

    /// <summary>
    /// Returns the configured library name for a projection.
    /// </summary>
    public static string LibraryNameFor(ProjectionKind kind, PluginConfiguration configuration) => kind switch
    {
        ProjectionKind.Origin => configuration.OriginLibraryName,
        ProjectionKind.Language => configuration.LanguageLibraryName,
        ProjectionKind.Year => configuration.YearLibraryName,
        ProjectionKind.Award => configuration.AwardLibraryName,
        ProjectionKind.Nomination => configuration.NominationLibraryName,
        ProjectionKind.List => configuration.ListLibraryName,
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
    /// Ceremony slugs whose display name title-casing cannot produce.
    /// </summary>
    private static readonly Dictionary<string, string> _ceremonyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oscar"] = "Oscar",
        ["bafta"] = "BAFTA",
        ["golden_globe"] = "Golden Globe",
        ["emmy"] = "Emmy"
    };

    /// <summary>
    /// List slugs and the names their collections carry.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than the settings page's labels, which are written to be
    /// unambiguous in a form; these are written to fit under a poster. They also avoid every
    /// character <see cref="BoxSetFolder.SanitiseName"/> replaces — notably the question mark
    /// in "They Shoot Pictures, Don't They?", which would leave a double space in the folder
    /// name.
    /// </remarks>
    private static readonly Dictionary<string, string> _listNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["imdb_top_250"] = "IMDb Top 250",
        ["sight_and_sound"] = "Sight & Sound 2022",
        ["afi_100"] = "AFI's 100 Years…100 Movies",
        ["bfi_top_100"] = "BFI Top 100 British Films",
        ["national_film_registry"] = "National Film Registry",
        ["criterion_collection"] = "The Criterion Collection",
        ["tspdt_1000"] = "TSPDT Top 1000"
    };

    /// <summary>
    /// Words that stay lowercase inside an award category, so
    /// <c>best_motion_picture_musical_or_comedy</c> does not come out as "… Musical Or
    /// Comedy". Never applied to the first word.
    /// </summary>
    private static readonly HashSet<string> _minorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to"
    };

    /// <summary>
    /// Segments of an award category that are acronyms, not words. Slugging flattened the
    /// case out of them upstream, and title-casing would put back the wrong one — the Golden
    /// Globes' television categories would read "Best Tv Series Drama".
    /// </summary>
    private static readonly Dictionary<string, string> _acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tv"] = "TV",
        ["bbc"] = "BBC",
        ["uk"] = "UK",
        ["us"] = "US",
        ["usa"] = "USA"
    };

    /// <summary>
    /// Gets the ceremony slugs Tagsmith knows a proper name for.
    /// </summary>
    /// <remarks>
    /// Exposed so a ceremony that appears in the dataset and the settings page but not here
    /// fails a test rather than shipping a collection called "Bafta". The four ceremonies are
    /// otherwise enumerated in three separate places that nothing cross-checks.
    /// </remarks>
    public static IReadOnlyCollection<string> NamedCeremonies => _ceremonyNames.Keys;

    /// <summary>
    /// Gets the curated-list slugs Tagsmith knows a proper name for. Same reason as
    /// <see cref="NamedCeremonies"/>.
    /// </summary>
    public static IReadOnlyCollection<string> NamedLists => _listNames.Keys;

    /// <summary>
    /// Turns a projected value into the name its collection carries.
    /// </summary>
    /// <remarks>
    /// Most namespaces hold a flat slug and title-case cleanly. Awards and nominations hold
    /// the two-part <c>ceremony:category</c> shape the tag uses, and lists hold a slug whose
    /// real name is not a title-casing of it.
    /// </remarks>
    public static string DisplayName(ProjectionKind kind, string value) => kind switch
    {
        ProjectionKind.Award or ProjectionKind.Nomination => AwardName(value),
        ProjectionKind.List => _listNames.TryGetValue(value, out var name) ? name : DisplayName(value),
        _ => DisplayName(value)
    };

    /// <summary>
    /// Renders <c>oscar:best_picture</c> as <c>Oscar – Best Picture</c>.
    /// </summary>
    /// <remarks>
    /// An en dash rather than the colon the tag uses, because
    /// <see cref="BoxSetFolder.SanitiseName"/> replaces a colon with a space and
    /// "Oscar  Best Picture" would carry the double space into the folder name and the
    /// collection title. A value with no colon — a tag added by hand under the same
    /// namespace — is named like any other slug.
    /// </remarks>
    private static string AwardName(string value)
    {
        var split = value.IndexOf(':', StringComparison.Ordinal);
        if (split <= 0 || split == value.Length - 1)
        {
            return DisplayName(value);
        }

        var ceremony = value[..split];
        var category = value[(split + 1)..];

        return string.Concat(
            _ceremonyNames.TryGetValue(ceremony, out var name) ? name : DisplayName(ceremony),
            " – ",
            CategoryName(category));
    }

    /// <summary>
    /// Title-cases an award category, leaving joining words lowercase.
    /// </summary>
    private static string CategoryName(string category)
    {
        var words = category.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return category;
        }

        return string.Join(
            ' ',
            words.Select((word, index) =>
            {
                if (_acronyms.TryGetValue(word, out var acronym))
                {
                    return acronym;
                }

                return index > 0 && _minorWords.Contains(word)
                    ? word.ToLowerInvariant()
                    : Capitalise(word);
            }));
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

        return string.Join(' ', words.Select(Capitalise));
    }

    /// <summary>
    /// Capitalises one word, leaving one that starts with a digit alone so <c>1950s</c>
    /// survives.
    /// </summary>
    private static string Capitalise(string word) =>
        char.IsDigit(word[0]) ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
