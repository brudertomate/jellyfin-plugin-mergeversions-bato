using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<MergeVersionsManager> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IVideoVersions _videoVersions;
        private readonly VersionLinkRepair _versionRepair;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        // Share of the task progress used by merging. The rest goes to choosing the primary versions.
        private const double MergeShare = 80;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem,
            IVideoVersions videoVersions,
            VersionLinkRepair versionRepair
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _videoVersions = videoVersions;
            _versionRepair = versionRepair;
        }

        /// <summary>What the latest run did. The configuration page shows it.</summary>
        public RunActivity Activity { get; } = new RunActivity();

        public Task MergeMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
            => RunAsync("Merge", () => MergeAndSetPrimaryAsync(progress, user, cancellationToken), progress, cancellationToken);

        public Task SplitMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
            => RunAsync("Split", () => SplitAsync(progress, user, cancellationToken), progress, cancellationToken);

        /// <summary>
        /// Runs one operation and records its outcome. A failing movie does not stop the run, but a run
        /// with failures still ends as failed, so Jellyfin's task list shows it too.
        /// </summary>
        private async Task RunAsync(string operation, Func<Task<int>> run, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!await _runLock.WaitAsync(0).ConfigureAwait(false))
                throw new InvalidOperationException("Another plugin operation is already running.");
            Activity.Start(operation);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var errors = await run().ConfigureAwait(false);
                if (errors > 0)
                {
                    throw new InvalidOperationException(
                        $"{errors} movie(s) failed. See the plugin's configuration page or the Jellyfin log for details.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Activity.Finish(RunState.Completed);
                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                Activity.Finish(RunState.Cancelled, "Stopped.");
                throw;
            }
            catch (Exception exception)
            {
                Activity.Finish(RunState.Failed, exception.Message);
                throw;
            }
            finally { _runLock.Release(); }
        }

        private async Task<int> MergeAndSetPrimaryAsync(IProgress<double> progress, ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Scanning for repeated movies");
            var errors = 0;

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(group => group.Count() > 1 &&
                group.Any(movie => movie.PrimaryVersionId == null &&
                                    !movie.LinkedAlternateVersions.Any()))
                .ToList();

            Activity.SetPhase("Merging", duplicateMovies.Count);
            Activity.Info(duplicateMovies.Count == 0
                ? "No new versions to merge."
                : $"Found {duplicateMovies.Count} movie(s) with new versions to merge.");

            var current = 0;
            foreach (var movies in duplicateMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = DisplayName(movies.First());
                Activity.SetCurrent(name);
                _logger.LogInformation("Merging {Name}", name);
                try
                {
                    await _videoVersions.MergeAsync(movies.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                    Activity.Merged(name, $"Merged {movies.Count()} versions: {string.Join(", ", movies.Select(FileName))}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    _logger.LogError(exception, "Could not merge {Name}", name);
                    Activity.Error(name, "Merge failed: " + exception.Message);
                }

                Activity.StepDone();
                progress?.Report(++current / (double)duplicateMovies.Count * MergeShare);
            }

            errors += await SetPreferredPrimaryVersionsAsync(GetMoviesFromLibrary(), progress, cancellationToken).ConfigureAwait(false);
            return errors;
        }

        /// <summary>
        /// Makes the configured resolution the preselected version of every merged movie.
        /// A failing or not yet analyzed item is skipped and retried on the next run.
        /// </summary>
        private async Task<int> SetPreferredPrimaryVersionsAsync(IEnumerable<Video> items, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var order = Plugin.Instance.PluginConfiguration.PrimaryResolutionOrder;
            var mergedItems = items
                .Where(item => item.PrimaryVersionId == null && item.LinkedAlternateVersions.Any())
                .ToList();

            Activity.SetPhase("Choosing primary versions", mergedItems.Count);
            var errors = 0;
            var current = 0;
            foreach (var primary in mergedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(MergeShare + (current++ / (double)mergedItems.Count * (100 - MergeShare)));
                var name = DisplayName(primary);
                Activity.SetCurrent(name);
                try
                {
                    await SetPreferredPrimaryVersionAsync(primary, name, order).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    _logger.LogError(exception, "Could not set the primary version of {Name}", primary.Name);
                    Activity.Error(name, "Setting the primary version failed: " + exception.Message);
                }

                Activity.StepDone();
            }

            return errors;
        }

        private async Task SetPreferredPrimaryVersionAsync(Video primary, string name, string order)
        {
            var versions = _libraryManager.GetLinkedAlternateVersions(primary)
                .Prepend(primary)
                .Where(video => !video.Video3DFormat.HasValue && video.VideoType == VideoType.VideoFile)
                .ToList();
            var widths = versions.Select(video => video.GetDefaultVideoStream()?.Width ?? 0).ToList();
            var index = PrimaryVersionSelector.SelectIndex(widths, order);
            if (index < 0)
            {
                _logger.LogInformation("Skipping {Name}: not all versions are analyzed yet", primary.Name);
                var waiting = versions.Where((_, i) => widths[i] <= 0).Select(FileName).ToList();
                Activity.Skipped(name, waiting.Count > 0
                    ? "Not analyzed yet, retried on the next run: " + string.Join(", ", waiting)
                    : "No playable video file found.");
                return;
            }

            var preferred = versions[index];
            var others = _libraryManager.GetLinkedAlternateVersions(primary)
                .Prepend(primary)
                .Where(video => !video.Id.Equals(preferred.Id))
                .ToList();

            if (!preferred.Id.Equals(primary.Id))
            {
                _logger.LogInformation("Making {Path} the primary version", preferred.Path);
                preferred.SetPrimaryVersionId(null);
                preferred.LinkedAlternateVersions = others
                    .Select(video => new LinkedChild { ItemId = video.Id, Type = LinkedChildType.LinkedAlternateVersion })
                    .ToArray();
                await preferred.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

                foreach (var other in others)
                {
                    other.SetPrimaryVersionId(preferred.Id);
                    other.LinkedAlternateVersions = Array.Empty<LinkedChild>();
                    await other.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                    // Primary selection must not reroute unrelated collection/playlist links.
                }

                Activity.PrimaryChanged(name, $"Primary version is now {ResolutionLabel(widths[index])}: {FileName(preferred)}");
            }
            else
            {
                Activity.PrimaryKept();
            }

            // The home page opens the newest version of a movie, so the primary has to be the newest.
            var dateCreated = PrimaryVersionSelector.GetPrimaryDateCreated(
                preferred.DateCreated,
                others.Select(video => video.DateCreated));
            if (dateCreated != preferred.DateCreated)
            {
                preferred.DateCreated = dateCreated;
                await preferred.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public VersionRepairReport VersionReport { get; private set; }

        public Task AnalyzeVersionLinksAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
            => RunAsync("Analyze", () => RepairVersionsAsync(false, true, progress, cancellationToken), progress, cancellationToken);

        public Task RepairOrphanedVersionsAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
            => RunAsync("Repair", () => RepairVersionsAsync(false, false, progress, cancellationToken), progress, cancellationToken);

        public Task PreviewSplitAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
            => RunAsync("Preview split", () => RepairVersionsAsync(true, true, progress, cancellationToken), progress, cancellationToken);

        private Task<int> SplitAsync(IProgress<double> progress, ClaimsPrincipal user, CancellationToken cancellationToken)
            => RepairVersionsAsync(true, false, progress, cancellationToken);

        private Task<int> RepairVersionsAsync(bool splitAll, bool dryRun, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (_libraryManager.IsScanRunning) throw new InvalidOperationException("Stop the library scan before analyzing or repairing versions.");
            VersionReport = null;
            Activity.SetPhase("Analyzing persisted version links", 0);
            var report = _versionRepair.Analyze(splitAll, dryRun, cancellationToken);
            VersionReport = report;
            Activity.VersionStats(report);
            Activity.Info($"Scanned {report.MoviesScanned} movies; {report.Groups.Count} groups; {report.InconsistentGroups} inconsistent; {report.SelectedGroups} selected. Dry-run: {dryRun}.");
            Activity.SetPhase(dryRun ? "Preview" : "Updating version links", report.Groups.Count);
            var errors = 0;
            var done = 0;
            foreach (var group in report.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = string.Join(", ", group.Movies.Select(m => $"{m.Name} [{m.Id}]"));
                Activity.SetCurrent(name);
                try
                {
                    foreach (var issue in group.Issues) _logger.LogWarning("Version links: {Issue}", issue);
                    if (group.Selected)
                    {
                        if (group.Blocked != null) throw new InvalidOperationException(group.Blocked);
                        if (dryRun)
                            Activity.Info($"Would split {name}. {string.Join(" ", group.Issues)}");
                        else
                        {
                            if (_libraryManager.IsScanRunning) throw new InvalidOperationException("A library scan started; group was not changed.");
                            _versionRepair.Apply(group, cancellationToken);
                            group.Status = "Changed";
                            Activity.Split(name, "Cleared version links; saved and verified the complete group.");
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    group.Status = "Failed";
                    group.Error = exception.ToString();
                    _logger.LogError(exception, "Could not repair version group {Name}", name);
                    Activity.Error(name, exception.Message);
                }
                Activity.StepDone();
                progress?.Report(++done / (double)Math.Max(1, report.Groups.Count) * 99);
            }
            return Task.FromResult(errors);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            // Read once per scan instead of once per movie.
            var libraryPaths = _libraryManager.GetVirtualFolders()
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .ToList();

            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .OfType<Movie>()
                // An empty id would group unrelated movies together.
                .Where(m => m.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
                .Where(m => !IsInInactiveLibrary(m, libraryPaths) && !IsInExcludedLibrary(m))
                .ToList();
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
           return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                  && Plugin.Instance.PluginConfiguration.LocationsExcluded
                    .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item, List<string> libraryPaths)
        {
            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            return !libraryPaths
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }

        private static string DisplayName(BaseItem item)
            => item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;

        private static string FileName(BaseItem item)
            => string.IsNullOrEmpty(item.Path) ? item.Name : Path.GetFileNameWithoutExtension(item.Path);

        private static string ResolutionLabel(int width)
        {
            var resolution = PrimaryVersionSelector.Classify(width);
            return resolution == "SD" ? "SD" : resolution + "p";
        }
    }
}
