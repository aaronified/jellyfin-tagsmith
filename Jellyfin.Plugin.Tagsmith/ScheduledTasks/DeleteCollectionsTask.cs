using Jellyfin.Plugin.Tagsmith.Collections;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Tagsmith.ScheduledTasks;

/// <summary>
/// Deletes every collection and library Tagsmith created, leaving media and tags alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Media is never touched.</b> A projected collection is a database item plus a tiny
/// <c>collection.xml</c> folder under Tagsmith's own directory; its members are linked
/// children, so deleting the collection deletes the link, never the film. Removing the
/// library removes its definition, not the files it pointed at. Tags stay too — they are
/// the source of truth, and projections that remain enabled rebuild from them on the next
/// sync.
/// </para>
/// <para>
/// Exists as a task rather than an API endpoint so the settings page can start it through
/// the same mechanism as the rescan button, and so progress and cancellation appear in
/// Dashboard &gt; Scheduled Tasks. It has no default trigger: this only ever runs when
/// asked.
/// </para>
/// </remarks>
public class DeleteCollectionsTask : IScheduledTask
{
    private readonly CollectionProjector _projector;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteCollectionsTask"/> class.
    /// </summary>
    public DeleteCollectionsTask(CollectionProjector projector)
    {
        _projector = projector;
    }

    /// <inheritdoc />
    public string Name => "Delete Tagsmith collections";

    /// <inheritdoc />
    public string Key => "TagsmithDeleteCollections";

    /// <inheritdoc />
    public string Description =>
        "Removes every collection and library Tagsmith created. Media files and tags are not touched; "
        + "enabled projections are rebuilt on the next sync.";

    /// <inheritdoc />
    public string Category => "Tagsmith";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _projector.TearDownAllAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
