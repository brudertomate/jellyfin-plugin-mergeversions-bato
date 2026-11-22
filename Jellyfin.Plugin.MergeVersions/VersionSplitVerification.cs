using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.MergeVersions;

public sealed partial class VersionLinkRepair
{
    /// <summary>Fresh, read-only inventory of both persisted link types, including path-only local versions.</summary>
    public VersionSplitVerification VerifySplit(Action<Movie, int, int> progress, CancellationToken cancellationToken)
    {
        var movies = GetAllMovies();
        var owners = _links.GetItemIdsWithAlternateVersions(movies.Select(m => m.Id).ToArray());
        var result = new VersionSplitVerification { MoviesScanned = movies.Count };
        for (var i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movie = movies[i];
            var finding = new VersionSplitFinding { Movie = VersionRepairItem.From(movie) };
            try
            {
                if (owners.Contains(movie.Id))
                {
                    finding.ManualVersionIds = _links.GetLinkedChildrenIds(movie.Id, (int)LinkedChildType.LinkedAlternateVersion).ToArray();
                    finding.LocalVersionIds = _links.GetLinkedChildrenIds(movie.Id, (int)LinkedChildType.LocalAlternateVersion).ToArray();
                }
                result.MoviesChecked++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                finding.Error = exception.ToString();
            }

            if (finding.Error != null || movie.PrimaryVersionId.HasValue || movie.LinkedAlternateVersions.Length > 0 ||
                movie.LocalAlternateVersions.Length > 0 || movie.OwnerId != Guid.Empty ||
                finding.ManualVersionIds.Length > 0 || finding.LocalVersionIds.Length > 0)
                result.Findings.Add(finding);
            progress?.Invoke(movie, i + 1, movies.Count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        result.CompletedUtc = DateTime.UtcNow;
        return result;
    }

    private List<Movie> GetAllMovies() => _repository.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie],
        Recursive = true,
        IncludeAlternateVersions = true,
        IncludeOwnedItems = true,
        GroupByPresentationUniqueKey = false,
        EnableGroupByMetadataKey = false,
    }).OfType<Movie>().ToList();
}

public sealed class VersionSplitVerification
{
    public DateTime? CompletedUtc { get; set; }
    public int MoviesScanned { get; set; }
    public int MoviesChecked { get; set; }
    public List<VersionSplitFinding> Findings { get; set; } = new();
    public int PrimaryReferences => Findings.Count(f => f.Movie.PrimaryVersionId.HasValue);
    public int ManualDatabaseLinks => Findings.Sum(f => f.ManualVersionIds.Length);
    public int LocalDatabaseLinks => Findings.Sum(f => f.LocalVersionIds.Length);
    public int LocalPathMovies => Findings.Count(f => f.Movie.LocalAlternateVersions.Length > 0);
    public int VersionListMovies => Findings.Count(f => f.Movie.AlternateLinks.Length > 0);
    public int OwnedMovies => Findings.Count(f => f.Movie.OwnerId != Guid.Empty);
    public int Errors => Findings.Count(f => f.Error != null);
    public bool IsClear => CompletedUtc.HasValue && MoviesScanned > 0 && MoviesChecked == MoviesScanned && Findings.Count == 0;
    public string Conclusion => IsClear
        ? "No movie version links remain at the time of this check. Keep automatic merging disabled; you can uninstall Merge Versions Bato and restart Jellyfin. This check does not validate other database foreign keys."
        : Errors > 0 || !CompletedUtc.HasValue || MoviesScanned == 0
            ? "Verification incomplete. Do not use this result as confirmation that every movie is split. See the verification report."
            : "Movie version links or ownership remain. Review the verification report before uninstalling. Manual groups can be split with Split all movies; local file versions are managed by Jellyfin and are not split by this plugin.";
}

public sealed class VersionSplitFinding
{
    public VersionRepairItem Movie { get; set; }
    public Guid[] ManualVersionIds { get; set; } = Array.Empty<Guid>();
    public Guid[] LocalVersionIds { get; set; } = Array.Empty<Guid>();
    public string Error { get; set; }
}
