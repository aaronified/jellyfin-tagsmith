using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Adopts a poster into <c>&lt;config&gt;/tagsmith/thumbnails/</c> the moment somebody sets
/// one on a collection Tagsmith owns.
/// </summary>
/// <remarks>
/// <para>
/// Adoption used to be part of the projection run, which meant a poster set in the library UI
/// was not backed up until the next full sync — a heavy, library-wide pass that is not worth
/// running for the sake of one image. Listening for the change instead makes the backup
/// immediate and takes the artwork question out of the scheduled task entirely.
/// </para>
/// <para>
/// <c>ILibraryManager.ItemUpdated</c> fires for every item in the library, so the handler is
/// written to cost as close to nothing as possible for the items that are not collections
/// Tagsmith made. The real work happens in <see cref="CollectionProjector.AdoptPoster"/>,
/// which also documents why it must not be pushed onto a worker thread.
/// </para>
/// </remarks>
public sealed class PosterAdoptionListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly CollectionProjector _projector;
    private readonly ILogger<PosterAdoptionListener> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterAdoptionListener"/> class.
    /// </summary>
    public PosterAdoptionListener(
        ILibraryManager libraryManager,
        CollectionProjector projector,
        ILogger<PosterAdoptionListener> logger)
    {
        _libraryManager = libraryManager;
        _projector = projector;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.LogDebug("Tagsmith: watching for collection poster changes");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether an update Jellyfin reported includes an image change.
    /// </summary>
    /// <remarks>
    /// <c>ItemUpdateType</c> is a <c>[Flags]</c> enum and one update carries several reasons
    /// at once — setting a poster through the web UI arrives as <c>ImageUpdate</c>, but a
    /// metadata refresh that also touched an image does not. So this is a mask, not an
    /// equality test. Note also that the server declares <c>None = 1</c>, not 0, which makes
    /// <c>HasFlag</c> and any "is it None" test read wrongly; only the <c>ImageUpdate</c> bit
    /// is ever consulted.
    /// </remarks>
    /// <param name="reason">The reason Jellyfin gave.</param>
    /// <returns>True when the item's images changed.</returns>
    public static bool IsImageChange(ItemUpdateType reason) => (reason & ItemUpdateType.ImageUpdate) != 0;

    /// <summary>
    /// Called on Jellyfin's own thread, inside <c>LibraryManager.UpdateItemsAsync</c>.
    /// </summary>
    /// <remarks>
    /// The server does wrap each handler in a try/catch of its own, but an exception escaping
    /// from here would still be logged as a server fault for something that is entirely
    /// Tagsmith's business, so it is caught and logged as Tagsmith's.
    /// </remarks>
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is null || !IsImageChange(e.UpdateReason))
            {
                return;
            }

            _projector.AdoptPoster(e.Item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tagsmith: could not adopt the poster on {Name}", e?.Item?.Name);
        }
    }
}
