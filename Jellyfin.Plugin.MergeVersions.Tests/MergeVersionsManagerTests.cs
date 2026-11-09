using System.Security.Claims;
using Jellyfin.Plugin.MergeVersions.Api;
using Jellyfin.Plugin.MergeVersions.Configuration;
using Jellyfin.Plugin.MergeVersions.ScheduledTasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class MergeVersionsManagerTests
{
    [Theory]
    [InlineData("MergeMovies")]
    [InlineData("SplitMovies")]
    public async Task ManualActionsWaitAndForwardUser(string operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), user, cancellation.Token)).Returns(completion.Task);
        versions.Setup(v => v.SplitAsync(It.IsAny<Guid>(), user, cancellation.Token)).Returns(completion.Task);
        // Split only touches merged movies, so it gets two merged pairs.
        var split = operation == "SplitMovies";
        var manager = CreateManager(versions, groups: split ? 2 : 1, merged: split);
        var controller = new MergeVersionsController(manager, NullLogger<MergeVersionsManager>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user, RequestAborted = cancellation.Token }
            }
        };

        var task = operation switch
        {
            "MergeMovies" => controller.MergeMoviesRequestAsync(),
            _ => controller.SplitMoviesRequestAsync()
        };

        Assert.False(task.IsCompleted);
        Assert.Single(versions.Invocations);
        completion.SetResult();
        Assert.IsType<NoContentResult>(await task);
        Assert.Equal(operation.StartsWith("Merge") ? 1 : 2, versions.Invocations.Count);
    }

    [Fact]
    public async Task ScheduledTaskWaitsForNativeMerge()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>())).Returns(completion.Task);
        var manager = CreateManager(versions);
        var progress = new RecordedProgress();
        var task = new MergeMoviesTask(manager, NullLogger<MergeVersionsManager>.Instance).Execute(default, progress);

        Assert.False(task.IsCompleted);
        Assert.DoesNotContain(100d, progress.Values);
        var ids = Assert.IsType<Guid[]>(Assert.Single(versions.Invocations).Arguments[0]);
        Assert.Equal(2, ids.Length);

        completion.SetResult();
        await task;
        Assert.Equal(100d, progress.Values.Last());
    }

    [Fact]
    public async Task CancellationStopsBeforeNextGroup()
    {
        using var cancellation = new CancellationTokenSource();
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, cancellation.Token))
            .Callback(() => cancellation.Cancel()).Returns(Task.CompletedTask);
        var manager = CreateManager(versions, groups: 2);
        var progress = new RecordedProgress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.MergeMoviesAsync(progress, cancellationToken: cancellation.Token));

        Assert.Single(versions.Invocations);
        Assert.DoesNotContain(100d, progress.Values);
    }

    [Fact]
    public async Task FailureIsNotReportedAsSuccessfulCompletion()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("native merge failed"));
        var manager = CreateManager(versions);
        var progress = new RecordedProgress();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeMoviesAsync(progress));

        Assert.DoesNotContain(100d, progress.Values);
    }

    [Fact]
    public void ManualEntryPointsRequireElevation()
    {
        var authorization = Assert.Single(typeof(MergeVersionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(Policies.RequiresElevation, authorization.Policy);
    }

    [Fact]
    public async Task FailingMovieIsRecordedAndTheRunGoesOn()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.SetupSequence(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("native merge failed"))
            .Returns(Task.CompletedTask);
        var manager = CreateManager(versions, groups: 2);
        var progress = new RecordedProgress();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeMoviesAsync(progress));

        Assert.Equal(2, versions.Invocations.Count);
        Assert.Contains("1 movie(s) failed", exception.Message);
        Assert.DoesNotContain(100d, progress.Values);
        var snapshot = manager.Activity.GetSnapshot();
        Assert.Equal("Merge", snapshot.Run.Operation);
        Assert.Equal(nameof(RunState.Failed), snapshot.Run.State);
        Assert.Equal(1, snapshot.Run.Errors);
        Assert.Equal(1, snapshot.Run.Merged);
        Assert.NotNull(snapshot.Run.EndedUtc);
        var error = Assert.Single(snapshot.Entries, e => e.Level == nameof(ActivityLevel.Error));
        Assert.Equal("Test video (2020)", error.Movie);
        Assert.Contains("native merge failed", error.Message);
    }

    [Fact]
    public async Task SuccessfulMergeIsRecorded()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var manager = CreateManager(versions);

        await manager.MergeMoviesAsync(null);

        var snapshot = manager.Activity.GetSnapshot();
        Assert.Equal(nameof(RunState.Completed), snapshot.Run.State);
        Assert.Equal(1, snapshot.Run.Merged);
        Assert.Equal(0, snapshot.Run.Errors);
        Assert.Null(snapshot.Run.Current);
        var merged = Assert.Single(snapshot.Entries, e => e.Level == nameof(ActivityLevel.Merged));
        Assert.Contains("2 versions", merged.Message);
        Assert.Contains("Movie 0b", merged.Message);
    }

    [Fact]
    public async Task CancelledRunIsRecorded()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var manager = CreateManager(new Mock<IVideoVersions>(MockBehavior.Strict));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.MergeMoviesAsync(null, cancellationToken: cancellation.Token));

        Assert.Equal(nameof(RunState.Cancelled), manager.Activity.GetSnapshot().Run.State);
    }

    [Fact]
    public async Task MoviesWithoutTmdbIdAreNotGrouped()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        var manager = CreateManager(versions, tmdb: _ => " ");

        await manager.MergeMoviesAsync(null);

        Assert.Empty(versions.Invocations);
    }

    [Fact]
    public async Task SplitSkipsMoviesThatAreNotMerged()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        var manager = CreateManager(versions, groups: 2);

        await manager.SplitMoviesAsync(null);

        Assert.Empty(versions.Invocations);
        Assert.Equal(nameof(RunState.Completed), manager.Activity.GetSnapshot().Run.State);
    }

    [Fact]
    public async Task StatusEndpointReturnsTheLatestRun()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var manager = CreateManager(versions);
        await manager.MergeMoviesAsync(null);
        var controller = new MergeVersionsController(manager, NullLogger<MergeVersionsManager>.Instance);

        var all = controller.GetStatus().Value!;
        var newer = controller.GetStatus(all.LastSequence).Value!;

        Assert.NotEmpty(all.Entries);
        Assert.Empty(newer.Entries);
        Assert.Equal(all.Run.Id, newer.Run.Id);
    }

    private static MergeVersionsManager CreateManager(
        Mock<IVideoVersions> versions, int groups = 1, bool merged = false, Func<int, string>? tmdb = null)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(AppContext.BaseDirectory);
        _ = new TestPlugin(paths.Object);

        var movies = Enumerable.Range(0, groups * 2).Select(index =>
        {
            var item = new Movie();
            item.Id = Guid.NewGuid();
            item.Name = "Test video";
            item.ProductionYear = 2020;
            item.Path = $"/movies/Movie {index / 2}{(index % 2 == 0 ? "a" : "b")}.mkv";
            item.ProviderIds["Tmdb"] = tmdb?.Invoke(index) ?? (index / 2).ToString();
            return item;
        }).ToList();
        if (merged)
        {
            for (var i = 0; i < movies.Count; i += 2)
            {
                movies[i].LinkedAlternateVersions = [new LinkedChild { ItemId = movies[i + 1].Id, Type = LinkedChildType.LinkedAlternateVersion }];
                movies[i + 1].SetPrimaryVersionId(movies[i].Id);
            }
        }

        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(movies.Cast<BaseItem>().ToList());
        library.Setup(l => l.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());
        return new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), versions.Object);
    }

    private sealed class RecordedProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    private sealed class TestPlugin : Plugin
    {
        public TestPlugin(IServerApplicationPaths paths) : base(paths, Mock.Of<IXmlSerializer>())
        {
            Configuration = new PluginConfiguration();
        }
    }
}
