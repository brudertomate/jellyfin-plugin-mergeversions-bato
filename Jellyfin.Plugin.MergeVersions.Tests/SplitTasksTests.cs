using Jellyfin.Plugin.MergeVersions.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class SplitTasksTests
{
    // The configuration page starts the split task by this key and it must not run on a schedule.
    [Fact]
    public void SplitTaskIsHiddenAndOnlyRunsOnRequest()
    {
        var task = (IScheduledTask)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SplitMoviesTask));
        var configurable = Assert.IsAssignableFrom<IConfigurableScheduledTask>(task);

        Assert.Equal("BatoSplitMoviesTask", task.Key);
        Assert.True(configurable.IsHidden);
        Assert.True(configurable.IsEnabled);
        Assert.Empty(task.GetDefaultTriggers());
    }
}
