# Tagsmith

A Jellyfin plugin that derives searchable, namespaced tags from media metadata, so you
can find things by origin, language, year, awards or list membership without tagging
anything by hand.

Target server: **Jellyfin 10.11.11** (net9.0).

## Tag shape

| Namespace     | Example                       | From                                              |
| ------------- | ----------------------------- | ------------------------------------------------- |
| `origin=`     | `origin=india`                | TMDb/TVDb, falling back to Jellyfin metadata      |
| `lang=`       | `lang=bengali`                | The **original language**, TMDb/TVDb first        |
| `audio_lang=` | `audio_lang=hindi`            | Audio tracks on the files (off by default)        |
| `year=`       | `year=1954`                   | Jellyfin metadata                                 |
| `award=`      | `award=oscar:best_picture`    | Embedded dataset, by IMDb id (off by default)     |
| `nomination=` | `nomination=bafta:best_film`  | Same dataset; nominations include the winner      |
| `list=`       | `list=imdb_top_250`           | Embedded dataset; pick lists individually         |

Values are slugified: lowercase, diacritics stripped, non-alphanumerics collapsed to `_`.
Award and list values keep a colon between ceremony and category. Tags outside the
configured namespaces are never touched.

Country names are canonicalised, so `United States of America`, `USA`, `Estados Unidos`
and `美国` all become `origin=united_states` — one tag, not four. Changing a value rewrites
the existing tag rather than adding another. Full detail in [docs/tagging.md](docs/tagging.md).

## Status

Alpha. Origin and language are looked up in TMDb (built into the server) and TVDb (needs
the official TheTVDB plugin) first, with Jellyfin's own metadata as the fallback. Awards
cover the Academy Awards in full plus BAFTA, Golden Globes and Emmys from Wikidata
(partial, winner-heavy). Seven curated lists ship as release-time snapshots. Third-party
data licensing is documented in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); see
[docs/plan.md](docs/plan.md) for what shipped when.

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

### Collection and library artwork

Optional, and not shipped with the plugin. A starter set — flags, native-script language
cards, decade cards, and a 16:9 home-screen tile per library — lives in
[assets/thumbnails](assets/thumbnails). Copy the ones you want into
`<config>/tagsmith/thumbnails/`, keeping the layout: collection posters go in
`<namespace>/` named after the tag value (`origin/india.png`), the library tile at the
root named after the namespace (`origin.png`). Case and separators do not matter.

A sync applies the folder wherever that cannot lose anything you did: collections and
libraries it just created, anything with no poster at all, and its own artwork when the
file changed on disk — so dropping images in and running a sync just works, whether the
collections exist yet or not. A poster you set by hand is never overwritten by a sync;
instead Tagsmith copies it into the folder the moment you set it, so it survives the
collection being rebuilt. **Reapply collection artwork** on the settings page is the one
trigger that goes the other way — it forces the folder onto everything, discarding
hand-set posters. Items with no matching file always keep what they have.

## Layout

```
Jellyfin.Plugin.Tagsmith/
  Plugin.cs                      plugin metadata + config page registration
  PluginServiceRegistrator.cs    DI registration — add new providers here
  Configuration/                 settings class + dashboard config page
  Data/                          generated: country dictionary, awards, curated lists
  External/
    IExternalMetadataSource.cs   the external-database contract, and its failure semantics
    TmdbMetadataSource.cs        the server's built-in TMDb client, via reflection
    TvdbMetadataSource.cs        the TheTVDB plugin's client, via reflection
  Tagging/
    ITagProvider.cs              the extension point
    TagNormalizer.cs             value slugification
    LanguageCodes.cs             source-specific language-code quirks
    TagAliasMap.cs               user rewrite rules
    CountryAliasCatalog.cs       country name canonicalisation
    CuratedData.cs               embedded awards + lists datasets, by IMDb id
    TagSynchronizer.cs           merge/prune logic, writes via ILibraryManager
  Collections/
    TagGrouping.cs               tags -> projected values, decade rollup
    BoxSetFolder.cs              the on-disk box set contract: folder name, collection.xml
    LibraryOwnership.cs          which libraries are Tagsmith's, and what to do about them
    MemberDiff.cs                collection membership diffing
    ThumbnailLocator.cs          user artwork lookup, per-value and per-library
    ArtworkPolicy.cs             which trigger does what about artwork
    ArtworkSynchronizer.cs       carries artwork both ways; owns the loop guard
    PosterAdoptionListener.cs    backs a hand-set poster up the moment it is set
    CollectionProjector.cs       library and collection reconciliation
  Providers/
    CoreMetadataTagProvider.cs   origin, language, audio languages, year
    AwardTagProvider.cs          award= and nomination=, from the embedded dataset
    ListTagProvider.cs           list=, from the embedded dataset
  ScheduledTasks/
    TagSyncTask.cs               full-library pass, then projection; nightly at 04:00
    ReapplyArtworkTask.cs        the reapply button, forces folder -> collections + tiles
assets/thumbnails/               starter artwork, downloaded separately
scripts/                         generators: countries, artwork, awards, lists
tests/                           xunit suite
```

## Adding a provider

Implement `ITagProvider`, declare the namespaces it owns, and register it in
`PluginServiceRegistrator`. `TagSynchronizer` handles merging and pruning.

## Development

```bash
dotnet build Jellyfin.Plugin.Tagsmith -c Release
dotnet test tests/Jellyfin.Plugin.Tagsmith.Tests

npm --prefix scripts install          # once, for all generators
node scripts/generate-countries.mjs   # CLDR country dictionary
node scripts/generate-awards.mjs      # awards dataset (network)
node scripts/generate-lists.mjs       # curated lists dataset (network)
node scripts/generate-artwork.mjs     # starter posters + library tiles
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
`<jellyfin-config>/plugins/Tagsmith_0.0.6/` and restart the server.

The `Jellyfin.Controller` package version in the csproj must match the server version,
or the plugin loads as `NotSupported`.

## Run it

Dashboard → Tagsmith (or Plugins → Tagsmith) to configure, then either hit **Rescan
library now** on that page or wait for the nightly 04:00 run. Progress appears under
Dashboard → Scheduled Tasks. Turn on dry run first to see what would change in the logs.

## License

MIT — see [LICENSE](LICENSE).

## Known gaps

- TVDb lookups need the official TheTVDB plugin installed; without it, TVDb-only series
  go through TMDb's find endpoint or fall back to Jellyfin metadata. Installing the plugin
  takes effect after a server restart.
- BAFTA, Golden Globe and Emmy data comes from Wikidata, which records awards on people
  more than on titles — coverage is partial, winner-heavy, and thinnest for acting and
  directing categories. The Academy Awards dataset is complete.
- No per-item trigger for **tagging**; the plugin assumes the web UI isn't used on client
  devices, so tagging happens on the schedule or on demand from the settings page. Only
  collection artwork reacts to a live change.
- Renaming a projection's library **in Tagsmith's settings** tears the old one down and
  rebuilds it, since Jellyfin 10.11 exposes no rename on `ILibraryManager`. Collections are
  regenerated, but per-user access grants are not. Renaming it in Jellyfin's own dashboard
  is fine — Tagsmith owns libraries by id and simply follows the new name.
- If the library name a projection wants is already taken by a library Tagsmith did not
  create, it refuses and logs an error rather than adopting it. Pick a different name.
