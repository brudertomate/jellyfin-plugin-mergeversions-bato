using Jellyfin.Plugin.MergeVersions.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class SplitTasksTests
{
    // The configuration page starts the split tasks by these keys and they must not run on a schedule.
    [Theory]
    [InlineData(typeof(SplitMoviesTask), "BatoSplitMoviesTask")]
    [InlineData(typeof(SplitEpisodesTask), "BatoSplitEpisodesTask")]
    public void SplitTasksAreHiddenAndOnlyRunOnRequest(Type type, string key)
    {
        var task = (IScheduledTask)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        var configurable = Assert.IsAssignableFrom<IConfigurableScheduledTask>(task);

        Assert.Equal(key, task.Key);
        Assert.True(configurable.IsHidden);
        Assert.True(configurable.IsEnabled);
        Assert.Empty(task.GetDefaultTriggers());
    }
}
