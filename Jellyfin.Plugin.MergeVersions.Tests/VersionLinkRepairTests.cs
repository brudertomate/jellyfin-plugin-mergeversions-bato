using Jellyfin.Plugin.MergeVersions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class VersionLinkRepairTests
{
    [Fact]
    public void BabyDriverWithoutPrimaryOrFilesIsRepairedAndRerunIsEmpty()
    {
        var movie = Movie("3D0672AD-14E6-7B2F-6222-AC4B51A58546");
        movie.Name = "Baby Driver";
        movie.PrimaryVersionId = Guid.Parse("3423C903-DFB8-B16D-6E09-5E9DAE62A3C0");
        var fixture = new Fixture(movie);
        var report = fixture.Service.Analyze(false, false, default);
        var group = Assert.Single(report.Groups);
        Assert.True(group.Selected);
        Assert.Contains(group.Issues, i => i.Contains("missing"));

        fixture.Service.Apply(group, default);

        Assert.Null(movie.PrimaryVersionId);
        Assert.Empty(movie.LinkedAlternateVersions);
        Assert.Empty(fixture.Service.Analyze(false, true, default).Groups);
        fixture.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public void QueryIncludesHiddenOwnedVirtualAndUnidentifiedMovies()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(movie);
        fixture.Service.Analyze(false, true, default);
        fixture.Repository.Verify(r => r.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.IncludeAlternateVersions && q.IncludeOwnedItems && !q.GroupByPresentationUniqueKey &&
            !q.EnableGroupByMetadataKey && q.IsVirtualItem == null)), Times.Once);
        fixture.Library.Verify(l => l.GetVirtualFolders(), Times.Never);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void HealthyGroupIsKeptByRepairAndSelectedBySplit()
    {
        var primary = Movie();
        var alternate = Movie();
        Link(primary, alternate);
        var fixture = new Fixture(primary, alternate);
        var healthy = Assert.Single(fixture.Service.Analyze(false, true, default).Groups);
        Assert.False(healthy.Selected);
        Assert.Empty(healthy.Issues);
        var split = Assert.Single(fixture.Service.Analyze(true, false, default).Groups);
        fixture.Service.Apply(split, default);
        Assert.Null(alternate.PrimaryVersionId);
        Assert.Empty(primary.LinkedAlternateVersions);
        fixture.Persistence.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 2), CancellationToken.None), Times.Once);
    }

    [Fact]
    public void MissingAlternateAndReverseOnlyReferencesAreFound()
    {
        var primary = Movie();
        var alternate = Movie();
        primary.LinkedAlternateVersions = [new() { ItemId = Guid.NewGuid(), Type = LinkedChildType.LinkedAlternateVersion }];
        alternate.PrimaryVersionId = primary.Id;
        var fixture = new Fixture(primary, alternate);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        Assert.Equal(2, group.Movies.Count);
        Assert.True(group.Selected);
        fixture.Service.Apply(group, default);
        Assert.Null(alternate.PrimaryVersionId);
        Assert.Empty(primary.LinkedAlternateVersions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CyclesAndSelfReferencesTerminate(bool self)
    {
        var a = Movie();
        var b = Movie();
        a.PrimaryVersionId = self ? a.Id : b.Id;
        if (!self) b.PrimaryVersionId = a.Id;
        var fixture = new Fixture(a, b);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        Assert.True(group.Selected);
        fixture.Service.Apply(group, default);
        Assert.Null(a.PrimaryVersionId);
        Assert.Null(b.PrimaryVersionId);
    }

    [Fact]
    public void SharedMissingPrimaryProducesOneTransaction()
    {
        var a = Movie();
        var b = Movie();
        a.PrimaryVersionId = b.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(a, b);
        Assert.Equal(2, Assert.Single(fixture.Service.Analyze(false, true, default).Groups).Movies.Count);
    }

    [Fact]
    public void UnexpectedLinkedChildTypesAreNeverPassedToPersistence()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var unrelated = new LinkedChild { ItemId = Guid.NewGuid(), Type = (LinkedChildType)0 };
        Assert.NotEqual(LinkedChildType.LinkedAlternateVersion, unrelated.Type);
        movie.LinkedAlternateVersions = [unrelated];
        var fixture = new Fixture(movie);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(Assert.Single(fixture.Service.Analyze(false, false, default).Groups), default));
        Assert.Same(unrelated, Assert.Single(movie.LinkedAlternateVersions));
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("local")]
    [InlineData("localLink")]
    public void LocalAndOwnedVersionsAreBlockedWithoutWrites(string kind)
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        if (kind == "owner") movie.OwnerId = Guid.NewGuid();
        if (kind == "local") movie.LocalAlternateVersions = ["/missing/local.mkv"];
        if (kind == "localLink") movie.LinkedAlternateVersions = [new() { ItemId = Guid.NewGuid(), Type = LinkedChildType.LocalAlternateVersion }];
        var fixture = new Fixture(movie);
        var group = Assert.Single(fixture.Service.Analyze(true, false, default).Groups);
        Assert.NotNull(group.Blocked);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default));
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void NonMovieReferenceIsBlocked()
    {
        var movie = Movie();
        var folder = new Folder { Id = Guid.NewGuid() };
        movie.PrimaryVersionId = folder.Id;
        var fixture = new Fixture(movie);
        fixture.Repository.Setup(r => r.RetrieveItem(folder.Id)).Returns(folder);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        Assert.NotNull(group.Blocked);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default));
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void ChangedPreviewIsRejected()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(movie);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        movie.PrimaryVersionId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default));
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void LocalDatabaseLinkWithoutSerializedPathBlocksWholeGroup()
    {
        var primary = Movie();
        var child = Movie();
        var fixture = new Fixture(primary, child);
        fixture.Links.Setup(l => l.GetItemIdsWithAlternateVersions(It.IsAny<IReadOnlyList<Guid>>())).Returns(new HashSet<Guid> { primary.Id });
        fixture.Links.Setup(l => l.GetLinkedChildrenIds(primary.Id, (int)LinkedChildType.LocalAlternateVersion)).Returns(new[] { child.Id });
        var group = Assert.Single(fixture.Service.Analyze(true, false, default).Groups);
        Assert.Equal(2, group.Movies.Count);
        Assert.NotNull(group.Blocked);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default));
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void LocalDatabaseLinkAddedAfterPreviewPreventsWrite()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(movie);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        fixture.Links.Setup(l => l.GetLinkedChildrenIds(movie.Id, (int)LinkedChildType.LocalAlternateVersion)).Returns(new[] { Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default));
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void FailedReadbackCannotBeReportedAsSuccessfulRepair()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(movie);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        var persisted = Movie(movie.Id.ToString());
        persisted.PrimaryVersionId = movie.PrimaryVersionId;
        fixture.Repository.SetupSequence(r => r.RetrieveItem(movie.Id)).Returns(movie).Returns(persisted);
        Assert.Contains("Post-save verification failed", Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default)).Message);
    }

    [Fact]
    public void TransactionFailureRestoresInMemoryGroup()
    {
        var primary = Movie();
        var alternate = Movie();
        Link(primary, alternate);
        var fixture = new Fixture(primary, alternate);
        var key = alternate.PresentationUniqueKey;
        fixture.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("foreign key constraint"));
        var group = Assert.Single(fixture.Service.Analyze(true, false, default).Groups);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Apply(group, default));
        Assert.Equal(primary.Id, alternate.PrimaryVersionId);
        Assert.Equal(key, alternate.PresentationUniqueKey);
        Assert.Equal(alternate.Id, Assert.Single(primary.LinkedAlternateVersions).ItemId);
    }

    [Fact]
    public async Task FailedGroupDoesNotPreventNextGroupAndIsNotCountedAsSuccess()
    {
        var a = Movie();
        var b = Movie();
        a.PrimaryVersionId = Guid.NewGuid();
        b.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(a, b);
        fixture.Persistence.SetupSequence(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("write failed"))
            .Pass();
        var manager = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RepairOrphanedVersionsAsync(null));
        Assert.Equal(1, manager.VersionReport.ChangedGroups);
        Assert.Equal(1, manager.VersionReport.FailedGroups);
        Assert.Equal(1, manager.Activity.GetSnapshot().Run.Split);
        Assert.Equal("Failed", manager.Activity.GetSnapshot().Run.State);
    }

    [Fact]
    public async Task DryRunDoesNotWriteAndReportsCandidates()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(movie);
        var manager = fixture.Manager();
        await manager.AnalyzeVersionLinksAsync(null);
        Assert.True(manager.VersionReport.DryRun);
        Assert.Equal(1, manager.VersionReport.SelectedGroups);
        Assert.Equal(0, manager.VersionReport.ChangedGroups);
        Assert.NotNull(movie.PrimaryVersionId);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ActiveLibraryScanPreventsRepair()
    {
        var fixture = new Fixture(Movie());
        fixture.Library.SetupGet(l => l.IsScanRunning).Returns(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager().RepairOrphanedVersionsAsync(null));
        fixture.Persistence.VerifyNoOtherCalls();
        fixture.Repository.VerifyNoOtherCalls();
    }

    [Fact]
    public void CancellationBeforeWriteLeavesGroupUntouched()
    {
        var movie = Movie();
        movie.PrimaryVersionId = Guid.NewGuid();
        var fixture = new Fixture(movie);
        var group = Assert.Single(fixture.Service.Analyze(false, false, default).Groups);
        Assert.Throws<OperationCanceledException>(() => fixture.Service.Apply(group, new CancellationToken(true)));
        Assert.NotNull(movie.PrimaryVersionId);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SplitVerificationConfirmsClearLibraryWithoutWriting()
    {
        var fixture = new Fixture(Movie(), Movie());
        var manager = fixture.Manager();
        await manager.VerifyAllMoviesSplitAsync(null);
        Assert.True(manager.SplitVerification.IsClear);
        Assert.Equal(2, manager.SplitVerification.MoviesChecked);
        Assert.Empty(manager.SplitVerification.Findings);
        Assert.True(manager.Activity.GetSnapshot().Run.AllMoviesSplit);
        Assert.Contains("uninstall", manager.Activity.GetSnapshot().Run.Message);
        fixture.Persistence.VerifyNoOtherCalls();
        fixture.Library.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("manualDatabase")]
    [InlineData("localDatabase")]
    [InlineData("localPath")]
    [InlineData("owner")]
    [InlineData("objectList")]
    public void SplitVerificationFindsEveryVersionRepresentation(string kind)
    {
        var movie = Movie();
        if (kind == "primary") movie.PrimaryVersionId = Guid.NewGuid();
        if (kind == "owner") movie.OwnerId = Guid.NewGuid();
        if (kind == "localPath") movie.LocalAlternateVersions = ["/missing/local-version.mkv"];
        if (kind == "objectList") movie.LinkedAlternateVersions = [new() { ItemId = Guid.NewGuid(), Type = LinkedChildType.LinkedAlternateVersion }];
        var fixture = new Fixture(movie);
        if (kind.EndsWith("Database"))
        {
            fixture.Links.Setup(l => l.GetItemIdsWithAlternateVersions(It.IsAny<IReadOnlyList<Guid>>())).Returns(new HashSet<Guid> { movie.Id });
            var type = kind == "manualDatabase" ? LinkedChildType.LinkedAlternateVersion : LinkedChildType.LocalAlternateVersion;
            fixture.Links.Setup(l => l.GetLinkedChildrenIds(movie.Id, (int)type)).Returns(new[] { Guid.NewGuid() });
        }
        var result = fixture.Service.VerifySplit(null, default);
        Assert.False(result.IsClear);
        Assert.Single(result.Findings);
        Assert.Equal(1, result.MoviesChecked);
        Assert.Equal(0, result.Errors);
        Assert.Contains("remain", result.Conclusion);
        if (kind == "manualDatabase") Assert.Equal(1, result.ManualDatabaseLinks);
        if (kind == "localDatabase") Assert.Equal(1, result.LocalDatabaseLinks);
        if (kind == "localPath") Assert.Equal(1, result.LocalPathMovies);
        if (kind == "primary") Assert.Equal(1, result.PrimaryReferences);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public void VerificationDoesNotCertifyEmptyLibrary()
    {
        var fixture = new Fixture();
        var result = fixture.Service.VerifySplit(null, default);
        Assert.False(result.IsClear);
        Assert.Contains("incomplete", result.Conclusion);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VerificationReadErrorContinuesButNeverCertifiesClear()
    {
        var a = Movie();
        var b = Movie();
        var fixture = new Fixture(a, b);
        fixture.Links.Setup(l => l.GetItemIdsWithAlternateVersions(It.IsAny<IReadOnlyList<Guid>>())).Returns(new HashSet<Guid> { a.Id });
        fixture.Links.Setup(l => l.GetLinkedChildrenIds(a.Id, (int)LinkedChildType.LinkedAlternateVersion)).Throws(new InvalidOperationException("read failed"));
        var manager = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAllMoviesSplitAsync(null));
        Assert.False(manager.SplitVerification.IsClear);
        Assert.Equal(1, manager.SplitVerification.MoviesChecked);
        Assert.Equal(1, manager.SplitVerification.Errors);
        Assert.Equal("Failed", manager.Activity.GetSnapshot().Run.State);
        Assert.Contains("read failed", Assert.Single(manager.SplitVerification.Findings).Error);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScanStartingDuringVerificationInvalidatesResult()
    {
        var fixture = new Fixture(Movie());
        fixture.Library.SetupSequence(l => l.IsScanRunning).Returns(false).Returns(true);
        var manager = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAllMoviesSplitAsync(null));
        Assert.False(manager.SplitVerification.IsClear);
        Assert.Null(manager.SplitVerification.CompletedUtc);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VerificationCancellationNeverPublishesClearResult()
    {
        var fixture = new Fixture(Movie());
        var manager = fixture.Manager();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.VerifyAllMoviesSplitAsync(null, new CancellationToken(true)));
        Assert.Null(manager.SplitVerification);
        fixture.Persistence.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProductiveRunInvalidatesPreviousVerification()
    {
        var fixture = new Fixture(Movie());
        var manager = fixture.Manager();
        await manager.VerifyAllMoviesSplitAsync(null);
        Assert.True(manager.SplitVerification.IsClear);
        await manager.SplitMoviesAsync(null);
        Assert.Null(manager.SplitVerification);
    }

    private static Movie Movie(string id = null) => new()
    {
        Id = id == null ? Guid.NewGuid() : Guid.Parse(id), Name = "Movie without TMDb",
        Path = "/missing/directory/movie.mkv",
    };

    private static void Link(Movie a, Movie b)
    {
        a.LinkedAlternateVersions = [new() { ItemId = b.Id, Type = LinkedChildType.LinkedAlternateVersion }];
        b.SetPrimaryVersionId(a.Id);
    }

    private sealed class Fixture
    {
        public Mock<IItemRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IItemPersistenceService> Persistence { get; } = new(MockBehavior.Strict);
        public Mock<ILibraryManager> Library { get; } = new(MockBehavior.Strict);
        public Mock<ILinkedChildrenService> Links { get; } = new();
        public VersionLinkRepair Service { get; }
        public Fixture(params Movie[] movies)
        {
            Repository.Setup(r => r.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(movies.Cast<BaseItem>().ToList());
            Repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((Guid id) => movies.SingleOrDefault(m => m.Id == id));
            Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()));
            Library.SetupGet(l => l.IsScanRunning).Returns(false);
            Library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => movies.SingleOrDefault(m => m.Id == id));
            Links.Setup(l => l.GetItemIdsWithAlternateVersions(It.IsAny<IReadOnlyList<Guid>>())).Returns(new HashSet<Guid>());
            Links.Setup(l => l.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>())).Returns(Array.Empty<Guid>());
            Service = new(Repository.Object, Persistence.Object, Library.Object, Links.Object);
        }
        public MergeVersionsManager Manager() => new(Library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), Mock.Of<IVideoVersions>(), Service);
    }
}
