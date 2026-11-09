using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class RunActivityTests
{
    [Fact]
    public void KeepsOnlyTheNewestEntries()
    {
        var activity = new RunActivity();
        activity.Start("Merge");
        for (var i = 0; i < RunActivity.Capacity + 20; i++)
        {
            activity.Skipped("Movie " + i, "not analyzed");
        }

        var snapshot = activity.GetSnapshot();

        Assert.Equal(RunActivity.Capacity, snapshot.Entries.Count);
        Assert.Equal("Movie 20", snapshot.Entries[0].Movie);
        Assert.Equal(RunActivity.Capacity + 20, snapshot.Run.Skipped);
        Assert.Equal(snapshot.Entries[^1].Sequence, snapshot.LastSequence);
    }

    [Fact]
    public void ReturnsOnlyEntriesAfterTheGivenSequence()
    {
        var activity = new RunActivity();
        activity.Start("Merge");
        activity.Merged("A", "merged");
        var first = activity.GetSnapshot();
        activity.Error("B", "failed");

        var next = activity.GetSnapshot(first.LastSequence);

        var entry = Assert.Single(next.Entries);
        Assert.Equal("B", entry.Movie);
        Assert.Equal(nameof(ActivityLevel.Error), entry.Level);
        Assert.Equal(1, next.Run.Errors);
    }

    [Fact]
    public void NewRunForgetsThePreviousOne()
    {
        var activity = new RunActivity();
        activity.Start("Merge");
        activity.Merged("A", "merged");
        activity.Finish(RunState.Completed);

        activity.Start("Split");
        activity.SetPhase("Splitting", 3);
        activity.SetCurrent("C");
        activity.StepDone();

        var snapshot = activity.GetSnapshot();
        Assert.Empty(snapshot.Entries);
        Assert.Equal("Split", snapshot.Run.Operation);
        Assert.Equal(nameof(RunState.Running), snapshot.Run.State);
        Assert.Equal(0, snapshot.Run.Merged);
        Assert.Equal("C", snapshot.Run.Current);
        Assert.Equal(1, snapshot.Run.Done);
        Assert.Equal(3, snapshot.Run.Total);
    }

    [Fact]
    public void SnapshotIsACopy()
    {
        var activity = new RunActivity();
        activity.Start("Merge");
        var snapshot = activity.GetSnapshot();

        activity.Merged("A", "merged");

        Assert.Equal(0, snapshot.Run.Merged);
    }

    [Fact]
    public void NothingIsRecordedBeforeTheFirstRun()
    {
        var activity = new RunActivity();
        activity.Error("A", "failed");

        var snapshot = activity.GetSnapshot();

        Assert.Null(snapshot.Run);
        Assert.Empty(snapshot.Entries);
    }
}
