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

Country names are canonicalised, so `United States of America`, `USA`, `Estados Unidos`
and `美国` all become `origin=united_states` — one tag, not four. Changing a value rewrites
the existing tag rather than adding another. Full detail in [docs/tagging.md](docs/tagging.md).

## Status

Alpha. Covers metadata Jellyfin already has — production countries, audio languages and
first release year — plus the collections projection. See [docs/plan.md](docs/plan.md) for
the roadmap (external providers, awards, curated lists).

## Finding tagged items

Jellyfin removed tags from global search in 10.10 for performance reasons. Filtering still
works: click a tag on any item page, use `/web/#/list.html?type=tag&tag=origin%3Dindia`, or
query `GET /Items?Recursive=true&Tags=origin%3Dindia`. There is no wildcard matching.

Some clients — Fladder, for one — neither show nor filter on tags at all. For those, turn
on the **collections projection**: each namespace becomes a browsable library of
collections built from the tags, which works anywhere.

```
Origins  →  India · Japan · France        Decades  →  1950s · 1960s · 1970s
```

Off by default. Tags stay the source of truth and collections are rebuilt from them, so a
tag you add by hand lands in its collection on the next run. Years project by decade while
`year=1954` tags stay precise. Requires Jellyfin to have write access to its config
directory. Full detail in [docs/collections.md](docs/collections.md).

### Collection artwork

Optional, and not shipped with the plugin. A starter set — flags, native-script language
cards, decade cards — lives in [assets/thumbnails](assets/thumbnails). Copy the ones you
want into `<config>/tagsmith/thumbnails/<namespace>/`, named after the tag value; case and
separators do not matter.

It works both ways: set a poster by hand on a collection and Tagsmith copies it into that
folder as the stored artwork for the value, so it survives the collection being rebuilt.

## Layout

```
Jellyfin.Plugin.Tagsmith/
  Plugin.cs                      plugin metadata + config page registration
  PluginServiceRegistrator.cs    DI registration — add new providers here
  Configuration/                 settings class + dashboard config page
  Data/countries.json.gz         generated CLDR country dictionary
  Tagging/
    ITagProvider.cs              the extension point
    TagNormalizer.cs             value slugification
    TagAliasMap.cs               user rewrite rules
    CountryAliasCatalog.cs       country name canonicalisation
    TagSynchronizer.cs           merge/prune logic, writes via ILibraryManager
  Collections/
    TagGrouping.cs               tags -> projected values, decade rollup
    BoxSetFolder.cs              the on-disk box set contract: folder name, collection.xml
    LibraryOwnership.cs          which libraries are Tagsmith's, and what to do about them
    MemberDiff.cs                collection membership diffing
    ThumbnailLocator.cs          user artwork lookup
    CollectionProjector.cs       library and collection reconciliation
  Providers/
    CoreMetadataTagProvider.cs   core provider, no network
  ScheduledTasks/
    TagSyncTask.cs               full-library pass, then projection
assets/thumbnails/               starter artwork, downloaded separately
scripts/                         CLDR and artwork generators, manifest updater
tests/                           xunit suite
```

## Adding a provider

Implement `ITagProvider`, declare the namespaces it owns, and register it in
`PluginServiceRegistrator`. `TagSynchronizer` handles merging and pruning.

## Development

```bash
dotnet build Jellyfin.Plugin.Tagsmith -c Release
dotnet test tests/Jellyfin.Plugin.Tagsmith.Tests

npm --prefix scripts install          # regenerate the country dictionary
node scripts/generate-countries.mjs
```

Claude agents in `.claude/agents/` cover the routine: `test` (haiku) after every change,
`validate` (opus) before a release, `commit-message` and `release-notes` (sonnet) from the
diffs. See [CLAUDE.md](CLAUDE.md).

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
`<jellyfin-config>/plugins/Tagsmith_0.0.5/` and restart the server.

The `Jellyfin.Controller` package version in the csproj must match the server version,
or the plugin loads as `NotSupported`.

## Run it

Dashboard → Tagsmith (or Plugins → Tagsmith) to configure, then either hit **Rescan
library now** on that page or wait for the nightly 04:00 run. Progress appears under
Dashboard → Scheduled Tasks. Turn on dry run first to see what would change in the logs.

## License

MIT — see [LICENSE](LICENSE).

## Known gaps

- `original_lang=` needs an external provider — Jellyfin doesn't store it.
- No per-item trigger; the plugin assumes the web UI isn't used on client devices, so
  tagging happens on the schedule or on demand from the settings page.
- Renaming a projection's library **in Tagsmith's settings** tears the old one down and
  rebuilds it, since Jellyfin 10.11 exposes no rename on `ILibraryManager`. Collections are
  regenerated, but per-user access grants are not. Renaming it in Jellyfin's own dashboard
  is fine — Tagsmith owns libraries by id and simply follows the new name.
- If the library name a projection wants is already taken by a library Tagsmith did not
  create, it refuses and logs an error rather than adopting it. Pick a different name.
