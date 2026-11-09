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
