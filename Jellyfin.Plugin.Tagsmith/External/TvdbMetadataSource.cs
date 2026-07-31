using System.Globalization;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.External;

/// <summary>
/// Reads original language and origin country from TVDb, through the official TheTVDB
/// plugin's <c>TvdbClientManager</c> — its DI singleton, its project API key and its
/// in-process cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>This source only works when the TheTVDB plugin is installed</b> (it is in the default
/// plugin catalogue). Without it, series matched only by TVDb ids are bridged through TMDb
/// by <see cref="TmdbMetadataSource"/> where possible, and otherwise fall back to
/// Jellyfin's own metadata. The settings page says so too.
/// </para>
/// <para>
/// Signatures transcribed from the plugin source at v22, the release targeting server
/// 10.11.x:
/// </para>
/// <code>
/// Task&lt;SeriesBaseRecord&gt;    GetSeriesByIdAsync(int tvdbId, string language, CancellationToken ct)
/// Task&lt;MovieExtendedRecord&gt; GetMovieExtendedByIdAsync(int tvdbId, CancellationToken ct)
/// </code>
/// <para>
/// The records carry <c>OriginalCountry</c> (TVDb's 3-letter code, e.g. <c>usa</c>) and
/// <c>OriginalLanguage</c> (639-2-ish, e.g. <c>eng</c>, with quirks like <c>zhtw</c> that
/// <see cref="Tagging.LanguageCodes"/> smooths over). The <c>language</c> parameter is
/// unused inside the plugin; <c>"en"</c> is passed for shape only.
/// </para>
/// </remarks>
public class TvdbMetadataSource : IExternalMetadataSource
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TvdbMetadataSource> _logger;
    private readonly Lazy<Client?> _client;

    /// <summary>
    /// Whether the failure warning has been logged; later failures log at Debug.
    /// </summary>
    private volatile bool _warnedFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvdbMetadataSource"/> class.
    /// </summary>
    public TvdbMetadataSource(IServiceProvider services, ILogger<TvdbMetadataSource> logger)
    {
        _services = services;
        _logger = logger;
        _client = new Lazy<Client?>(Locate, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Name => "TVDb";

    /// <inheritdoc />
    public async Task<ExternalItemInfo?> GetAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var client = _client.Value;
        if (client is null || !TryGetId(item, out var tvdbId))
        {
            return null;
        }

        try
        {
            var record = item switch
            {
                Series => await Reflected.ResultOf(
                        client.GetSeries.Invoke(client.Manager, [tvdbId, "en", cancellationToken]))
                    .ConfigureAwait(false),
                Movie when client.GetMovie is not null => await Reflected.ResultOf(
                        client.GetMovie.Invoke(client.Manager, [tvdbId, cancellationToken]))
                    .ConfigureAwait(false),
                _ => null
            };

            if (record is null)
            {
                return null;
            }

            var country = Reflected.GetString(record, "OriginalCountry");

            return new ExternalItemInfo(
                Reflected.GetString(record, "OriginalLanguage"),
                string.IsNullOrWhiteSpace(country) ? [] : [country]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Failure is not evidence about the item — see ExternalLookupException.
            if (!_warnedFailure)
            {
                _warnedFailure = true;
                _logger.LogWarning(
                    ex,
                    "Tagsmith: TVDb lookups are failing (first: {Name}); affected items keep their existing tags",
                    item.Name);
            }

            throw new ExternalLookupException($"TVDb lookup failed for {item.Name}", ex);
        }
    }

    private static bool TryGetId(BaseItem item, out int id)
    {
        id = 0;
        var raw = item.GetProviderId(MetadataProvider.Tvdb);
        return !string.IsNullOrWhiteSpace(raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>
    /// Finds the TVDb plugin's client, if the plugin is installed at all. Runs once; the
    /// outcome is logged either way so "why are my TVDb series untagged" is answerable
    /// from the log.
    /// </summary>
    private Client? Locate()
    {
        // Locate must never throw: Lazy(ExecutionAndPublication) caches an exception and
        // rethrows it for every item forever, and resolving the manager constructs the
        // plugin's singleton, which is not Tagsmith's code to vouch for.
        try
        {
            return LocateCore();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tagsmith: could not reach the TheTVDB plugin's client; TVDb lookups are off");
            return null;
        }
    }

    private Client? LocateCore()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jellyfin.Plugin.Tvdb", StringComparison.Ordinal));

        var managerType = assembly?.GetType("Jellyfin.Plugin.Tvdb.TvdbClientManager");
        if (managerType is null)
        {
            _logger.LogInformation(
                "Tagsmith: the TheTVDB plugin is not installed; TVDb lookups are off and TVDb-matched series "
                + "use the TMDb bridge or Jellyfin metadata");
            return null;
        }

        var manager = _services.GetService(managerType);
        var getSeries = Reflected.Method(managerType, "GetSeriesByIdAsync", typeof(int), typeof(string), typeof(CancellationToken));

        if (manager is null || getSeries is null)
        {
            _logger.LogWarning(
                "Tagsmith: the installed TheTVDB plugin no longer matches the surface Tagsmith was built against; TVDb lookups are off");
            return null;
        }

        // The movie method is optional: absent on an older plugin, series still work.
        var getMovie = Reflected.Method(managerType, "GetMovieExtendedByIdAsync", typeof(int), typeof(CancellationToken));

        _logger.LogInformation("Tagsmith: using the TheTVDB plugin's client");
        return new Client(manager, getSeries, getMovie);
    }

    private sealed record Client(object Manager, MethodInfo GetSeries, MethodInfo? GetMovie);
}
