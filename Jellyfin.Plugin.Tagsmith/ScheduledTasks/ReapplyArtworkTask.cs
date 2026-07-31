using Jellyfin.Plugin.Tagsmith.Collections;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Tagsmith.ScheduledTasks;

/// <summary>
/// Applies the artwork in the thumbnails folder to every projected collection, replacing
/// whatever poster is on them.
/// </summary>
/// <remarks>
/// Exists as a task rather than an API endpoint so the settings page can start it through
/// the same mechanism as the rescan button, and so progress and cancellation appear in
/// Dashboard &gt; Scheduled Tasks. It has no default trigger: this only ever runs when
/// asked.
/// </remarks>
public class ReapplyArtworkTask : IScheduledTask
{
    private readonly CollectionProjector _projector;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReapplyArtworkTask"/> class.
    /// </summary>
    public ReapplyArtworkTask(CollectionProjector projector)
    {
        _projector = projector;
    }

    /// <inheritdoc />
    public string Name => "Reapply Tagsmith collection artwork";

    /// <inheritdoc />
    public string Key => "TagsmithReapplyArtwork";

    /// <inheritdoc />
    public string Description =>
        "Replaces the poster on every projected collection and library tile with the matching image from the thumbnails folder.";

    /// <inheritdoc />
    public string Category => "Tagsmith";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _projector.ReapplyArtworkAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
