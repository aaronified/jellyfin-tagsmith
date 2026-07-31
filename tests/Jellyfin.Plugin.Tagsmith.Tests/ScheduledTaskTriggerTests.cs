using Jellyfin.Plugin.Tagsmith.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// Both constructors only assign their arguments, so the trigger tables can be read without
/// a server.
/// </summary>
public class ScheduledTaskTriggerTests
{
    [Fact]
    public void The_sync_task_runs_nightly_at_four()
    {
        var triggers = new TagSyncTask(null!, null!, null!, null!).GetDefaultTriggers().ToArray();

        var trigger = Assert.Single(triggers);
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(4).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void The_reapply_artwork_task_only_runs_when_asked() =>
        // It replaces posters set by hand, so nothing may start it but the button.
        Assert.Empty(new ReapplyArtworkTask(null!).GetDefaultTriggers());

    [Fact]
    public void The_delete_collections_task_only_runs_when_asked() =>
        // It tears down every projection; a schedule that ran it on its own would delete
        // and rebuild the user's libraries nightly.
        Assert.Empty(new DeleteCollectionsTask(null!).GetDefaultTriggers());
}
