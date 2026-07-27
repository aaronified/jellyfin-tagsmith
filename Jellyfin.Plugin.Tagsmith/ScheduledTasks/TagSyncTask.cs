using System.Diagnostics;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Tagsmith.Collections;
using Jellyfin.Plugin.Tagsmith.Tagging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.ScheduledTasks;

/// <summary>
/// Walks every movie and series and refreshes its Tagsmith tags.
/// </summary>
public class TagSyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly TagSynchronizer _synchronizer;
    private readonly CollectionProjector _projector;
    private readonly ILogger<TagSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagSyncTask"/> class.
    /// </summary>
    public TagSyncTask(
        ILibraryManager libraryManager,
        TagSynchronizer synchronizer,
        CollectionProjector projector,
        ILogger<TagSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _synchronizer = synchronizer;
        _projector = projector;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Tagsmith tags";

    /// <inheritdoc />
    public string Key => "TagsmithSync";

    /// <inheritdoc />
    public string Description => "Regenerates namespaced metadata tags for movies and shows.";

    /// <inheritdoc />
    public string Category => "Tagsmith";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true
        });

        _logger.LogInformation("Tagsmith: processing {Count} items", items.Count);

        var tagging = Stopwatch.StartNew();
        var changed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _synchronizer.SyncAsync(items[i], cancellationToken).ConfigureAwait(false))
            {
                changed++;
            }

            // Tagging is the bulk of the work; leave the last tenth for the projection.
            progress.Report(90.0 * (i + 1) / items.Count);
        }

        tagging.Stop();
        _logger.LogInformation(
            "Tagsmith: updated {Changed} of {Total} items in {Elapsed}",
            changed,
            items.Count,
            tagging.Elapsed);

        var projecting = Stopwatch.StartNew();
        await _projector.ProjectAsync(items, cancellationToken).ConfigureAwait(false);
        projecting.Stop();

        progress.Report(100);
        _logger.LogInformation("Tagsmith: projected collections in {Elapsed}", projecting.Elapsed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs nightly by default; the schedule is editable in Dashboard &gt; Scheduled Tasks.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    ];
}
