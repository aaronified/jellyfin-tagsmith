# Tagsmith

A Jellyfin plugin that derives searchable, namespaced tags from media metadata, so you
can find things by origin, language, year, awards or list membership without tagging
anything by hand.

Target server: **Jellyfin 10.11.11** (net9.0).

## Tag shape

| Namespace        | Example                      |
| ---------------- | ---------------------------- |
| `origin=`        | `origin=india`               |
| `lang=`          | `lang=bengali`               |
| `original_lang=` | `original_lang=japanese`     |
| `year=`          | `year=1954`                  |
| `award=`         | `award=oscar:best_picture`   |
| `nomination=`    | `nomination=bafta:best_actor`|
| `list=`          | `list=imdb_top_250`          |

Values are slugified: lowercase, diacritics stripped, non-alphanumerics collapsed to `_`.
Tags outside the configured namespaces are never touched.

## Status

v1.0 (this scaffold) covers only metadata Jellyfin already has: production countries,
audio languages and first release year. See [docs/plan.md](docs/plan.md) for the roadmap
(v1.1 external providers, v1.2 awards, v1.3 curated lists).

## Layout

```
Jellyfin.Plugin.Tagsmith/
  Plugin.cs                      plugin metadata + config page registration
  PluginServiceRegistrator.cs    DI registration — add new providers here
  Configuration/                 settings class + dashboard config page
  Tagging/
    ITagProvider.cs              the extension point
    TagNormalizer.cs             value slugification
    TagSynchronizer.cs           merge/prune logic, writes via ILibraryManager
  Providers/
    CoreMetadataTagProvider.cs   v1.0 provider, no network
  ScheduledTasks/
    TagSyncTask.cs               full-library pass
```

## Adding a provider

Implement `ITagProvider`, declare the namespaces it owns, and register it in
`PluginServiceRegistrator`. `TagSynchronizer` handles merging and pruning.

## Install and update

Add this repository once, in Dashboard → Plugins → Repositories:

```
https://raw.githubusercontent.com/aaronified/jellyfin-tagsmith/main/manifest.json
```

Tagsmith then appears in the plugin catalogue, and later versions show an **Update**
button in the dashboard — no file copying between test runs.

Publishing a new version: run the **Release** workflow in GitHub Actions with a version
number. It builds, attaches a zip to a GitHub release, and appends the entry (with md5
checksum) to `manifest.json`. Bump `targetAbi` in `build.yaml` when you move to a new
server version.

## Build locally

```bash
dotnet publish Jellyfin.Plugin.Tagsmith -c Release -o artifacts
```

Copy `artifacts/Jellyfin.Plugin.Tagsmith.dll` into
`<jellyfin-config>/plugins/Tagsmith_1.0.0.0/` and restart the server.

The `Jellyfin.Controller` package version in the csproj must match the server version,
or the plugin loads as `NotSupported`.

## Run it

Dashboard → Tagsmith (or Plugins → Tagsmith) to configure, then either hit **Rescan
library now** on that page or wait for the nightly 04:00 run. Progress appears under
Dashboard → Scheduled Tasks. Turn on dry run first to see what would change in the logs.

## Known gaps

- `original_lang=` needs an external provider (v1.1) — Jellyfin doesn't store it.
- No per-item trigger; the plugin assumes the web UI isn't used on client devices, so
  tagging happens on the schedule or on demand from the settings page.
