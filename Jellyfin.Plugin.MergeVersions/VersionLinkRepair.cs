using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

#pragma warning disable CS0618 // Retain legacy LinkedChild.Path in diagnostics and preview comparisons.

namespace Jellyfin.Plugin.MergeVersions;

/// <summary>Plans from persisted objects, without resolving paths or invoking media deletion.</summary>
public sealed class VersionLinkRepair
{
    private readonly IItemRepository _repository;
    private readonly IItemPersistenceService _persistence;
    private readonly ILibraryManager _library;
    private readonly ILinkedChildrenService _links;

    public VersionLinkRepair(IItemRepository repository, IItemPersistenceService persistence, ILibraryManager library, ILinkedChildrenService links)
    {
        _repository = repository;
        _persistence = persistence;
        _library = library;
        _links = links;
    }

    public VersionRepairReport Analyze(bool splitAll, bool dryRun, CancellationToken cancellationToken)
    {
        var movies = _repository.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie],
            Recursive = true,
            IncludeAlternateVersions = true,
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            EnableGroupByMetadataKey = false,
        }).OfType<Movie>().ToDictionary(m => m.Id);
        var owners = _links.GetItemIdsWithAlternateVersions(movies.Keys.ToArray());
        var localOwners = new HashSet<Guid>();
        var adjacency = new Dictionary<Guid, HashSet<Guid>>();
        void Edge(Guid a, Guid b)
        {
            if (!adjacency.ContainsKey(a)) adjacency[a] = new();
            if (!adjacency.ContainsKey(b)) adjacency[b] = new();
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        foreach (var movie in movies.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (movie.PrimaryVersionId is Guid primary) Edge(movie.Id, primary);
            foreach (var link in movie.LinkedAlternateVersions.Where(IsVersionLink))
            {
                Edge(movie.Id, link.ItemId ?? movie.Id);
            }
            if (owners.Contains(movie.Id))
                foreach (var localId in _links.GetLinkedChildrenIds(movie.Id, (int)LinkedChildType.LocalAlternateVersion))
                {
                    localOwners.Add(movie.Id);
                    Edge(movie.Id, localId);
                }
        }

        var report = new VersionRepairReport { DryRun = dryRun, SplitAll = splitAll, MoviesScanned = movies.Count };
        var visited = new HashSet<Guid>();
        foreach (var id in adjacency.Keys.OrderBy(id => id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(id)) continue;
            var pending = new Stack<Guid>();
            var ids = new List<Guid>();
            pending.Push(id);
            while (pending.TryPop(out var next))
            {
                ids.Add(next);
                foreach (var neighbor in adjacency[next])
                    if (visited.Add(neighbor)) pending.Push(neighbor);
            }

            var group = new VersionRepairGroup();
            foreach (var memberId in ids)
            {
                if (!movies.TryGetValue(memberId, out var movie))
                {
                    var other = _repository.RetrieveItem(memberId);
                    group.Issues.Add($"Referenced item {memberId} is {(other == null ? "missing" : "not a movie")}.");
                    if (other != null) group.Blocked = "References a non-movie; manual investigation required.";
                    continue;
                }

                group.Movies.Add(VersionRepairItem.From(movie));
                if (localOwners.Contains(movie.Id) || movie.OwnerId != Guid.Empty || movie.LocalAlternateVersions.Length > 0 ||
                    movie.LinkedAlternateVersions.Any(l => l.Type == LinkedChildType.LocalAlternateVersion))
                    group.Blocked = "Contains owned/local file versions; automatic file grouping must be handled by Jellyfin.";
                if (movie.LinkedAlternateVersions.Any(l => !IsVersionLink(l)))
                    group.Blocked = "Unexpected relationship type in LinkedAlternateVersions; manual investigation required.";
                if (movie.PrimaryVersionId is Guid primary)
                {
                    if (primary == movie.Id || !movies.TryGetValue(primary, out var parent) ||
                        parent.PrimaryVersionId.HasValue ||
                        !parent.LinkedAlternateVersions.Any(l => IsVersionLink(l) && l.ItemId == movie.Id))
                        group.Issues.Add($"{movie.Id}: inconsistent PrimaryVersionId {primary}.");
                }

                var targets = movie.LinkedAlternateVersions.Where(IsVersionLink).ToList();
                if (targets.GroupBy(l => l.ItemId).Any(g => g.Count() > 1))
                    group.Issues.Add($"{movie.Id}: duplicate alternate references.");
                foreach (var link in targets)
                    if (!link.ItemId.HasValue || link.ItemId == movie.Id || movie.PrimaryVersionId.HasValue ||
                        !movies.TryGetValue(link.ItemId.Value, out var child) || child.PrimaryVersionId != movie.Id)
                        group.Issues.Add($"{movie.Id}: inconsistent alternate reference {link.ItemId} ({link.Path}).");
            }

            group.Selected = splitAll || group.Issues.Count > 0;
            group.Status = group.Blocked != null ? "Blocked" : group.Selected ? "Planned" : "Healthy";
            report.Groups.Add(group);
        }

        return report;
    }

    public void Apply(VersionRepairGroup group, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (group.Blocked != null) throw new InvalidOperationException(group.Blocked);
        // Re-read before writing. Never overwrite a group that changed after its preview.
        var movies = group.Movies.Select(snapshot =>
        {
            var movie = _repository.RetrieveItem(snapshot.Id) as Movie;
            if (movie == null || !snapshot.Matches(movie))
                throw new InvalidOperationException($"Version links changed for {snapshot.Id}; analyze again.");
            if (_links.GetLinkedChildrenIds(movie.Id, (int)LinkedChildType.LocalAlternateVersion).Count > 0)
                throw new InvalidOperationException($"Local version links exist for {movie.Id}; no changes made.");
            return movie;
        }).ToList();
        var oldLinks = movies.Select(m => m.LinkedAlternateVersions).ToArray();
        var oldKeys = movies.Select(m => m.PresentationUniqueKey).ToArray();
        try
        {
            foreach (var movie in movies)
            {
                movie.SetPrimaryVersionId(null);
                movie.LinkedAlternateVersions = movie.LinkedAlternateVersions.Where(l => !IsVersionLink(l)).ToArray();
            }

            // Jellyfin 12.1 SaveItems uses one transaction and preserves non-version LinkedChildren.
            // Do not use UpdateItemsAsync: metadata savers and local path resolution are unnecessary here.
            _persistence.SaveItems(movies.Cast<BaseItem>().ToArray(), CancellationToken.None);
        }
        catch
        {
            for (var i = 0; i < movies.Count; i++)
            {
                movies[i].PrimaryVersionId = group.Movies[i].PrimaryVersionId;
                movies[i].PresentationUniqueKey = oldKeys[i];
                movies[i].LinkedAlternateVersions = oldLinks[i];
            }
            throw;
        }

        foreach (var movie in movies)
        {
            var persisted = _repository.RetrieveItem(movie.Id) as Movie;
            if (persisted == null || persisted.PrimaryVersionId.HasValue || persisted.LinkedAlternateVersions.Any(IsVersionLink))
                throw new InvalidOperationException($"Post-save verification failed for {movie.Id}; inspect the database before retrying.");
            // Persistence bypasses library notifications; update any cached instance as well.
            if (_library.GetItemById(movie.Id) is Movie cached)
            {
                cached.SetPrimaryVersionId(null);
                cached.LinkedAlternateVersions = persisted.LinkedAlternateVersions;
            }
        }
    }

    private static bool IsVersionLink(LinkedChild link) => link.Type == LinkedChildType.LinkedAlternateVersion;
}

public sealed class VersionRepairReport
{
    public bool DryRun { get; set; }
    public bool SplitAll { get; set; }
    public int MoviesScanned { get; set; }
    public List<VersionRepairGroup> Groups { get; set; } = new();
    public int InconsistentGroups => Groups.Count(g => g.Issues.Count > 0);
    public int SelectedGroups => Groups.Count(g => g.Selected);
    public int ChangedGroups => Groups.Count(g => g.Status == "Changed");
    public int FailedGroups => Groups.Count(g => g.Status == "Failed" || (g.Selected && g.Blocked != null));
}

public sealed class VersionRepairGroup
{
    public List<VersionRepairItem> Movies { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public string Blocked { get; set; }
    public bool Selected { get; set; }
    public string Status { get; set; }
    public string Error { get; set; }
}

public sealed class VersionRepairItem
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public Guid? PrimaryVersionId { get; set; }
    public Guid OwnerId { get; set; }
    public string[] LocalAlternateVersions { get; set; }
    public string[] AlternateLinks { get; set; }

    public static VersionRepairItem From(Movie movie) => new()
    {
        Id = movie.Id, Name = movie.Name, Path = movie.Path, PrimaryVersionId = movie.PrimaryVersionId,
        OwnerId = movie.OwnerId, LocalAlternateVersions = movie.LocalAlternateVersions.ToArray(),
        AlternateLinks = movie.LinkedAlternateVersions.Select(l => $"{l.Type}|{l.ItemId}|{l.Path}").OrderBy(s => s).ToArray(),
    };

    public bool Matches(Movie movie)
    {
        var current = From(movie);
        return PrimaryVersionId == current.PrimaryVersionId && OwnerId == current.OwnerId &&
            LocalAlternateVersions.SequenceEqual(current.LocalAlternateVersions) && AlternateLinks.SequenceEqual(current.AlternateLinks);
    }
}
