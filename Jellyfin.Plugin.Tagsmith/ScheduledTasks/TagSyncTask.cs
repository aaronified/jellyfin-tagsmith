using Jellyfin.Data.Enums;
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
    private readonly ILogger<TagSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagSyncTask"/> class.
    /// </summary>
    public TagSyncTask(
        ILibraryManager libraryManager,
        TagSynchronizer synchronizer,
        ILogger<TagSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _synchronizer = synchronizer;
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

        var changed = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _synchronizer.SyncAsync(items[i], cancellationToken).ConfigureAwait(false))
            {
                changed++;
            }

            progress.Report(100.0 * (i + 1) / items.Count);
        }

        _logger.LogInformation("Tagsmith: updated {Changed} items", changed);
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
