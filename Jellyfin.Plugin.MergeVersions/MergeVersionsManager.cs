using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly Timer _timer;
        private readonly ILogger<MergeVersionsManager> _logger; // TODO logging
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
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public async Task MergeMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated movies");

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(group => group.Count() > 1 &&
                group.Any(movie => movie.PrimaryVersionId == null &&
                                    !movie.LinkedAlternateVersions.Any()))
                .ToList();

            var current = 0;
            foreach (var movies in duplicateMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = movies.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(movies.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateMovies.Count * MergeShare);
            }

            await SetPreferredPrimaryVersionsAsync(GetMoviesFromLibrary(), progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        /// <summary>
        /// Makes the configured resolution the preselected version of every merged movie or episode.
        /// A failing or not yet analyzed item is skipped and retried on the next run.
        /// </summary>
        private async Task SetPreferredPrimaryVersionsAsync(IEnumerable<Video> items, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var order = Plugin.Instance.PluginConfiguration.PrimaryResolutionOrder;
            var mergedItems = items
                .Where(item => item.PrimaryVersionId == null && item.LinkedAlternateVersions.Any())
                .ToList();

            var current = 0;
            foreach (var primary in mergedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(MergeShare + (current++ / (double)mergedItems.Count * (100 - MergeShare)));
                try
                {
                    await SetPreferredPrimaryVersionAsync(primary, order).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Could not set the primary version of {Name}", primary.Name);
                }
            }
        }

        private async Task SetPreferredPrimaryVersionAsync(Video primary, string order)
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

        public async Task SplitMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movies = GetMoviesFromLibrary();
            var current = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {Name} ({Year})", movie.Name, movie.ProductionYear);
                await _videoVersions.SplitAsync(movie.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)movies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated episodes");

            var episodes = GetEpisodesFromLibrary();
            var duplicateEpisodes = episodes
                .GroupBy(GetEpisodeMergeKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1 &&
                                group.Any(episode => episode.PrimaryVersionId == null &&
                                                     !episode.LinkedAlternateVersions.Any()))
                .ToList();

            _logger.LogInformation(
                "Found {EpisodeCount} episodes and {DuplicateGroupCount} duplicate episode groups",
                episodes.Count,
                duplicateEpisodes.Count);

            var current = 0;
            foreach (var episodeGroup in duplicateEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = episodeGroup.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(episodeGroup.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateEpisodes.Count * MergeShare);
            }

            await SetPreferredPrimaryVersionsAsync(GetEpisodesFromLibrary(), progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {EpisodeNumber} ({SeriesName})", episode.IndexNumber, episode.SeriesName);
                await _videoVersions.SplitAsync(episode.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)episodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .Select(m => m as Movie)
                .Where(m => m.ProviderIds.ContainsKey("Tmdb"))
                .Where(IsEligible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .Select(m => m as Episode)
                .Where(IsEligible)
                .ToList();
        }

        private static string GetEpisodeMergeKey(Episode episode)
        {
            if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
            {
                return $"number:{episode.SeriesName}:{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd}";
            }

            foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
            {
                if (episode.ProviderIds.TryGetValue(provider, out var providerId)
                    && !string.IsNullOrWhiteSpace(providerId))
                {
                    return $"provider:{episode.SeriesName}:{provider}:{providerId}";
                }
            }

            return $"title:{episode.SeriesName}:{episode.SeasonName}:{episode.Name}:{episode.ProductionYear}";
        }

        private bool IsEligible(BaseItem item)
        {
            if (IsInInactiveLibrary(item) || IsInExcludedLibrary(item))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
           return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                  && Plugin.Instance.PluginConfiguration.LocationsExcluded
                    .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var virtualFolders = _libraryManager.GetVirtualFolders();

            return !virtualFolders
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private void OnTimerElapsed() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
            }
        }
    }
}
