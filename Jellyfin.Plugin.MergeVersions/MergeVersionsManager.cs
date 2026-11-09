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

        // Share of the task progress used by merging. The rest goes to choosing the primary versions.
        private const double MergeShare = 80;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem,
            IVideoVersions videoVersions
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _videoVersions = videoVersions;
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
                    await _libraryManager.RerouteLinkedChildReferencesAsync(other.Id, preferred.Id).ConfigureAwait(false);
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

        private async Task<int> SplitAsync(IProgress<double> progress, ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            var allMovies = GetMoviesFromLibrary();
            // Splitting a primary also frees its versions. So only primaries need a call, plus versions
            // whose primary is not in the list (for example because it lies in an excluded path).
            var primaryIds = allMovies
                .Where(movie => movie.PrimaryVersionId == null && movie.LinkedAlternateVersions.Any())
                .Select(movie => movie.Id)
                .ToHashSet();
            var movies = allMovies
                .Where(movie => primaryIds.Contains(movie.Id) ||
                    (movie.PrimaryVersionId is Guid primaryId && !primaryIds.Contains(primaryId)))
                .ToList();

            Activity.SetPhase("Splitting", movies.Count);
            Activity.Info(movies.Count == 0 ? "No merged movies found." : $"Found {movies.Count} merged movie(s) to split.");
            var errors = 0;
            var current = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = DisplayName(movie);
                Activity.SetCurrent(name);
                _logger.LogInformation("Splitting {Name}", name);
                try
                {
                    await _videoVersions.SplitAsync(movie.Id, user, cancellationToken).ConfigureAwait(false);
                    Activity.Split(name, "Split into single movies.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    _logger.LogError(exception, "Could not split {Name}", name);
                    Activity.Error(name, "Split failed: " + exception.Message);
                }

                Activity.StepDone();
                progress?.Report(++current / (double)movies.Count * 100);
            }

            return errors;
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
