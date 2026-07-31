using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Tagsmith.External;

/// <summary>
/// What an external database knows about one item, reduced to the fields Tagsmith tags on.
/// Values are raw, exactly as the source returned them — ISO codes, TVDb codes or English
/// names — and are normalised by the consumer, not here.
/// </summary>
/// <param name="OriginalLanguage">
/// The original-language code, e.g. TMDb's ISO 639-1 <c>bn</c> or TVDb's 639-2-ish
/// <c>ben</c>. Null when the source does not know.
/// </param>
/// <param name="Countries">
/// Production or origin countries — ISO 3166-1 alpha-2 (<c>IN</c>), TVDb alpha-3
/// (<c>ind</c>) or English names, whichever the source uses. Empty when unknown.
/// </param>
public sealed record ExternalItemInfo(
    string? OriginalLanguage,
    IReadOnlyList<string> Countries)
{
    /// <summary>
    /// Gets a value indicating whether the record carries anything usable at all.
    /// </summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(OriginalLanguage) && Countries.Count == 0;

    /// <summary>
    /// Gets a value indicating whether both fields are filled, i.e. no later source could
    /// add anything.
    /// </summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(OriginalLanguage) && Countries.Count > 0;

    /// <summary>
    /// Folds a later source's answer into an earlier one, field by field. The earlier
    /// source wins wherever it answered — sources are consulted in priority order — and
    /// the later one only fills gaps.
    /// </summary>
    public static ExternalItemInfo Merge(ExternalItemInfo? accumulated, ExternalItemInfo next)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (accumulated is null)
        {
            return next;
        }

        return new ExternalItemInfo(
            string.IsNullOrWhiteSpace(accumulated.OriginalLanguage) ? next.OriginalLanguage : accumulated.OriginalLanguage,
            accumulated.Countries.Count == 0 ? next.Countries : accumulated.Countries);
    }
}

/// <summary>
/// Thrown when a source's lookup <em>failed</em> — network trouble, a rate limit, an API
/// error — as opposed to the source not knowing the item.
/// </summary>
/// <remarks>
/// The distinction is load-bearing. "Doesn't know the item" is evidence, and the consumer
/// may fall back to Jellyfin's own metadata. "The lookup failed" is not evidence about the
/// item at all, and falling back on it would rewrite correct external tags to the fallback
/// values on every transient outage — thousands of writes from one bad network night, then
/// thousands more putting them back.
/// </remarks>
public sealed class ExternalLookupException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalLookupException"/> class.
    /// </summary>
    public ExternalLookupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalLookupException"/> class.
    /// </summary>
    public ExternalLookupException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalLookupException"/> class.
    /// </summary>
    public ExternalLookupException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A metadata database Tagsmith can ask about an item — TMDb through the server's built-in
/// client, TVDb through the official plugin's. Sources are tried in registration order and
/// the first answer wins per field; Jellyfin's own metadata is the fallback when none
/// answers.
/// </summary>
/// <remarks>
/// Implementations reach the client through reflection, because neither
/// <c>MediaBrowser.Providers</c> nor another plugin's assembly can be referenced at compile
/// time. They must therefore fail soft — a missing assembly or a renamed method reads as
/// "source unavailable", never as a crash — while still telling a broken lookup apart from
/// an unknown item; see <see cref="ExternalLookupException"/>.
/// </remarks>
public interface IExternalMetadataSource
{
    /// <summary>
    /// Gets the source name, used in logs.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Looks the item up. Returns null when the source does not apply — not installed, no
    /// id it recognises, an item type it does not cover, or the database has no record.
    /// </summary>
    /// <exception cref="ExternalLookupException">The lookup itself failed.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    Task<ExternalItemInfo?> GetAsync(BaseItem item, CancellationToken cancellationToken);
}
