using System.Globalization;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.External;

/// <summary>
/// Reads original language and production countries from TMDb, through the server's own
/// <c>TmdbClientManager</c> — the same singleton, API key and one-hour response cache the
/// built-in TMDb metadata providers use.
/// </summary>
/// <remarks>
/// <para>
/// <c>MediaBrowser.Providers</c> is not published as a package, so the client cannot be
/// referenced at compile time; everything here is reflection against signatures transcribed
/// from the server source at the pinned <c>targetAbi</c> (10.11.11):
/// </para>
/// <code>
/// Task&lt;Movie?&gt;  GetMovieAsync(int tmdbId, string? language, string? imageLanguages, string? countryCode, CancellationToken ct)
/// Task&lt;TvShow?&gt; GetSeriesAsync(int tmdbId, string? language, string? imageLanguages, string? countryCode, CancellationToken ct)
/// Task&lt;FindContainer?&gt; FindByExternalIdAsync(string externalId, FindExternalSource source, string language, string? countryCode, CancellationToken ct)
/// </code>
/// <para>
/// Binding is by exact parameter-type list, so a future server that changes a signature
/// reads as "TMDb unavailable" — logged once — rather than a crash mid-scan.
/// </para>
/// </remarks>
public class TmdbMetadataSource : IExternalMetadataSource
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TmdbMetadataSource> _logger;
    private readonly Lazy<Client?> _client;

    /// <summary>
    /// Whether the failure warning has been logged. Failures after the first log at Debug;
    /// a scan of ten thousand items must not write ten thousand warnings.
    /// </summary>
    private volatile bool _warnedFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbMetadataSource"/> class.
    /// </summary>
    public TmdbMetadataSource(IServiceProvider services, ILogger<TmdbMetadataSource> logger)
    {
        _services = services;
        _logger = logger;

        // Resolved on first use rather than at construction: this source is built when the
        // DI container assembles Tagsmith's services, and the server's own registrations
        // must not be assumed complete at that moment.
        _client = new Lazy<Client?>(Locate, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Name => "TMDb";

    /// <inheritdoc />
    public async Task<ExternalItemInfo?> GetAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var client = _client.Value;
        if (client is null)
        {
            return null;
        }

        try
        {
            return item switch
            {
                Movie => await GetMovieAsync(client, item, cancellationToken).ConfigureAwait(false),
                Series => await GetSeriesAsync(client, item, cancellationToken).ConfigureAwait(false),
                _ => null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network trouble, a rate limit, a TMDb outage. Failure is not evidence about
            // the item, so it is surfaced as such rather than returned as "unknown" — the
            // consumer keeps the item's existing tags instead of rewriting them from the
            // fallback. Warned once so an outage night is visible at default log level.
            if (!_warnedFailure)
            {
                _warnedFailure = true;
                _logger.LogWarning(
                    ex,
                    "Tagsmith: TMDb lookups are failing (first: {Name}); affected items keep their existing tags",
                    item.Name);
            }

            throw new ExternalLookupException($"TMDb lookup failed for {item.Name}", ex);
        }
    }

    // ---------------------------------------------------------------- lookups

    private static async Task<ExternalItemInfo?> GetMovieAsync(Client client, BaseItem item, CancellationToken cancellationToken)
    {
        if (!TryGetId(item, MetadataProvider.Tmdb, out var tmdbId))
        {
            return null;
        }

        var movie = await Reflected.ResultOf(
                client.GetMovie.Invoke(client.Manager, [tmdbId, null, null, null, cancellationToken]))
            .ConfigureAwait(false);

        return movie is null ? null : Read(movie, originCountryFirst: false);
    }

    private static async Task<ExternalItemInfo?> GetSeriesAsync(Client client, BaseItem item, CancellationToken cancellationToken)
    {
        if (!TryGetId(item, MetadataProvider.Tmdb, out var tmdbId))
        {
            // No TMDb id — a TVDb-matched series. Bridge the TVDb id to the TMDb record
            // so these libraries work even without the TVDb plugin installed.
            var bridged = await TryBridgeTvdbIdAsync(client, item, cancellationToken).ConfigureAwait(false);
            if (bridged is not int bridgedId)
            {
                return null;
            }

            tmdbId = bridgedId;
        }

        var show = await Reflected.ResultOf(
                client.GetSeries.Invoke(client.Manager, [tmdbId, null, null, null, cancellationToken]))
            .ConfigureAwait(false);

        return show is null ? null : Read(show, originCountryFirst: true);
    }

    /// <summary>
    /// Resolves a series that carries only a TVDb id to its TMDb record, through TMDb's
    /// find-by-external-id endpoint. This is what keeps TVDb-matched libraries working
    /// even without the TVDb plugin installed.
    /// </summary>
    private static async Task<object?> TryBridgeTvdbIdAsync(Client client, BaseItem item, CancellationToken cancellationToken)
    {
        var tvdbId = item.GetProviderId(MetadataProvider.Tvdb);
        if (string.IsNullOrWhiteSpace(tvdbId))
        {
            return null;
        }

        var container = await Reflected.ResultOf(
                client.FindByExternalId.Invoke(client.Manager, [tvdbId, client.TvdbSource, "en", null, cancellationToken]))
            .ConfigureAwait(false);

        var results = Reflected.Get(container, "TvResults") as System.Collections.IEnumerable;
        foreach (var result in results ?? Array.Empty<object>())
        {
            return Reflected.Get(result, "Id");
        }

        return null;
    }

    /// <summary>
    /// Reduces a TMDbLib movie or show object to the fields Tagsmith tags on.
    /// </summary>
    /// <param name="record">The TMDbLib object.</param>
    /// <param name="originCountryFirst">
    /// TV shows carry <c>origin_country</c> — where the show was made — which is the better
    /// answer for the origin tag than the co-producing companies' countries. Movies only
    /// have <c>production_countries</c>.
    /// </param>
    private static ExternalItemInfo Read(object record, bool originCountryFirst)
    {
        var countries = new List<string>();

        if (originCountryFirst && Reflected.Get(record, "OriginCountry") is System.Collections.IEnumerable origins)
        {
            foreach (var origin in origins)
            {
                if (origin is string code && !string.IsNullOrWhiteSpace(code))
                {
                    countries.Add(code);
                }
            }
        }

        if (countries.Count == 0 && Reflected.Get(record, "ProductionCountries") is System.Collections.IEnumerable produced)
        {
            foreach (var country in produced)
            {
                // The English name first — it reads better when canonicalisation is off —
                // and the ISO code as the fallback; the alias catalog resolves either.
                var value = Reflected.GetString(country, "Name") ?? Reflected.GetString(country, "Iso_3166_1");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    countries.Add(value);
                }
            }
        }

        return new ExternalItemInfo(Reflected.GetString(record, "OriginalLanguage"), countries);
    }

    private static bool TryGetId(BaseItem item, MetadataProvider provider, out int id)
    {
        id = 0;
        var raw = item.GetProviderId(provider);
        return !string.IsNullOrWhiteSpace(raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Finds the server's TMDb client and the three methods Tagsmith calls. Runs once;
    /// failure is logged once and the source stays unavailable for the process lifetime.
    /// </summary>
    private Client? Locate()
    {
        // Locate must never throw: Lazy(ExecutionAndPublication) caches an exception and
        // rethrows it for every item forever, and resolving the manager constructs the
        // server's singleton, which is not Tagsmith's code to vouch for.
        try
        {
            return LocateCore();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tagsmith: could not reach the server's TMDb client; TMDb lookups are off");
            return null;
        }
    }

    private Client? LocateCore()
    {
        var managerType = Type.GetType("MediaBrowser.Providers.Plugins.Tmdb.TmdbClientManager, MediaBrowser.Providers");
        var findSourceType = Type.GetType("TMDbLib.Objects.Find.FindExternalSource, TMDbLib");

        if (managerType is null || findSourceType is null)
        {
            _logger.LogWarning("Tagsmith: the server's TMDb client was not found; TMDb lookups are off");
            return null;
        }

        var manager = _services.GetService(managerType);
        var getMovie = Reflected.Method(
            managerType, "GetMovieAsync", typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken));
        var getSeries = Reflected.Method(
            managerType, "GetSeriesAsync", typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken));
        var find = Reflected.Method(
            managerType, "FindByExternalIdAsync", typeof(string), findSourceType, typeof(string), typeof(string), typeof(CancellationToken));

        if (manager is null || getMovie is null || getSeries is null || find is null)
        {
            _logger.LogWarning(
                "Tagsmith: the server's TMDb client no longer matches the surface Tagsmith was built against; TMDb lookups are off");
            return null;
        }

        _logger.LogInformation("Tagsmith: using the server's built-in TMDb client");

        // FindExternalSource.TvDb = 1 in the TMDbLib the server ships.
        return new Client(manager, getMovie, getSeries, find, Enum.ToObject(findSourceType, 1));
    }

    private sealed record Client(
        object Manager,
        MethodInfo GetMovie,
        MethodInfo GetSeries,
        MethodInfo FindByExternalId,
        object TvdbSource);
}
