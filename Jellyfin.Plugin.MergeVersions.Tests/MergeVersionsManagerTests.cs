using System.Security.Claims;
using Jellyfin.Plugin.MergeVersions.Api;
using Jellyfin.Plugin.MergeVersions.Configuration;
using Jellyfin.Plugin.MergeVersions.ScheduledTasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
        using var manager = CreateManager(versions);
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
        using var manager = CreateManager(versions);
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
        using var manager = CreateManager(versions, groups: 2);
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
        using var manager = CreateManager(versions);
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

    private static MergeVersionsManager CreateManager(Mock<IVideoVersions> versions, int groups = 1)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(AppContext.BaseDirectory);
        _ = new TestPlugin(paths.Object);

        var items = Enumerable.Range(0, groups * 2).Select(index =>
        {
            var item = new Movie();
            item.Id = Guid.NewGuid();
            item.Name = "Test video";
            item.ProviderIds["Tmdb"] = (index / 2).ToString();
            return (BaseItem)item;
        }).ToList();
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);
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
