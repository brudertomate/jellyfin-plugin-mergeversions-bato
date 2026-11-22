<h1 align="center">Merge Versions Bato</h1>

A fork of [danieladov/jellyfin-plugin-mergeversions](https://github.com/danieladov/jellyfin-plugin-mergeversions).
It merges repeated movies into one item with several versions, like the original, and adds a
preferred primary version. It only handles movies: Jellyfin 12 already groups episode versions that
lie in the same season folder by itself.

## What this fork adds

- **Preferred primary version:** after merging, the version with the preferred resolution becomes
  the preselected one. The order is set on the configuration page
  (default `1080,720,2160,SD`).
- **Home page:** the preferred version gets the newest creation date, so "Latest" opens it too.
- **Live status:** while "Merge all movies" or "Split all movies" runs, the configuration page shows
  the movie in progress, what was merged, which primary version was set, skipped movies (for example
  not analyzed yet) and errors with their message, then a summary. A failing movie no longer stops the
  run; the run still ends as failed. The last 500 entries stay in memory until Jellyfin restarts
  (API: `GET /MergeVersionsBato/Status?after=<sequence>`).
- **Own identity:** its own name, GUID, API route (`MergeVersionsBato`) and task keys. Updates of the
  original plugin can not overwrite it.

Uninstall the original Merge Versions plugin before installing this one. Otherwise both plugins merge.

## Install

Version 1.5.1.0 adds **Verify all movies are split**, an always read-only final check before
uninstalling. It checks primary references, manual and local database version links, local paths
and ownership across all movies, with a clear conclusion and a downloadable verification report.
Keep automatic merging disabled until uninstalling. An empty library or an incomplete check
never confirms that all movies are split.

Version 1.5.0.0 adds **Analyze version links**, **Repair orphaned versions**, and a repository-based
**Split all movies** with dry-run enabled by default, progress and a complete JSON report.
Repair/split inspect every persisted movie, including excluded libraries and movies without TMDb IDs.
Repair separates inconsistent groups; healthy groups stay merged. Local/owned file versions are
blocked for manual investigation. Requires Jellyfin 12.1+ with a compatible ABI.
Before productive repair, stop scans and make a complete database backup.
See [repair behavior, 404 analysis, safety limits and validation](docs/version-repair.md).

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**, add
   `https://raw.githubusercontent.com/brudertomate/jellyfin-plugin-mergeversions-bato/main/manifest.json`
2. Install **Merge Versions Bato** from the catalog.
3. Restart Jellyfin.

## Release

Push a tag like `v1.0.1.0`. The release workflow runs the tests, builds the DLL, publishes the release
and adds it to `manifest.json`.

## Build locally

```sh
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet test -c Release && dotnet publish Jellyfin.Plugin.MergeVersions -c Release -o /src/out"
```

## License

MIT, see [LICENSE](LICENSE). Original work by danieladov.
