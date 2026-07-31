using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// One title's awards record: the categories it won and the ones it was nominated in.
/// Values are final tag values — <c>oscar:best_picture</c> — pre-slugged per segment by
/// the generator, so nothing downstream re-normalises them.
/// </summary>
public sealed class AwardRecord
{
    /// <summary>Gets or sets the categories won.</summary>
    [JsonPropertyName("w")]
    public string[] Wins { get; set; } = [];

    /// <summary>
    /// Gets or sets the categories nominated in. Winners appear here too — a winner was a
    /// nominee — so this set is complete on its own.
    /// </summary>
    [JsonPropertyName("n")]
    public string[] Nominations { get; set; } = [];
}

/// <summary>
/// The embedded award and curated-list datasets, keyed by IMDb id.
/// </summary>
/// <remarks>
/// <para>
/// Both files are generated, never hand-edited: <c>scripts/generate-awards.mjs</c> and
/// <c>scripts/generate-lists.mjs</c> build them from their upstream sources (see the
/// scripts for provenance and licensing) and gzip them into <c>Data/</c>. Lookups are
/// purely local — no network at tag time.
/// </para>
/// <para>
/// Keys are lowercase IMDb title ids (<c>tt0111161</c>). A missing file loads as an empty
/// dataset rather than failing the tag pass, so a build without the data degrades to "no
/// award or list tags" instead of breaking.
/// </para>
/// </remarks>
public static class CuratedData
{
    private const string AwardsResource = "Jellyfin.Plugin.Tagsmith.Data.awards.json.gz";
    private const string ListsResource = "Jellyfin.Plugin.Tagsmith.Data.lists.json.gz";

    private static readonly Lazy<IReadOnlyDictionary<string, AwardRecord>> _awards =
        new(() => Load<AwardRecord>(AwardsResource));

    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> _lists =
        new(() => Load<string[]>(ListsResource));

    /// <summary>
    /// Gets the number of titles with award records, for logs and tests.
    /// </summary>
    public static int AwardTitleCount => _awards.Value.Count;

    /// <summary>
    /// Gets the number of titles on at least one curated list.
    /// </summary>
    public static int ListTitleCount => _lists.Value.Count;

    /// <summary>
    /// Gets every award record, so tests can sweep the whole schema rather than sample it.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, AwardRecord>> AllAwards => _awards.Value;

    /// <summary>
    /// Gets every list record, for the same reason.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string[]>> AllLists => _lists.Value;

    /// <summary>
    /// Looks up a title's award record, or null.
    /// </summary>
    public static AwardRecord? AwardsFor(string? imdbId) =>
        Normalise(imdbId) is { } key && _awards.Value.TryGetValue(key, out var record) ? record : null;

    /// <summary>
    /// Looks up the curated lists a title sits on. Empty when it is on none.
    /// </summary>
    public static IReadOnlyList<string> ListsFor(string? imdbId) =>
        Normalise(imdbId) is { } key && _lists.Value.TryGetValue(key, out var lists) ? lists : [];

    /// <summary>
    /// Extracts the <c>tt…</c> id from whatever form the provider id arrived in — bare,
    /// uppercase, or wrapped in a URL by an NFO writer. No id, no lookup.
    /// </summary>
    private static string? Normalise(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var match = Regex.Match(imdbId, @"tt\d+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static IReadOnlyDictionary<string, T> Load<T>(string resourceName)
    {
        // Degrades to empty on a missing OR unreadable resource. Lazy<T> caches a factory
        // exception and rethrows it on every call forever, which would turn one corrupt
        // build into a tag pass that dies on the first item of every run.
        try
        {
            using var stream = typeof(CuratedData).GetTypeInfo().Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return new Dictionary<string, T>();
            }

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);

            return JsonSerializer.Deserialize<Dictionary<string, T>>(gzip)
                   ?? new Dictionary<string, T>();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return new Dictionary<string, T>();
        }
    }
}
