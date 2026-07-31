# Jellyfin Metadata Tag Mapper Plugin Plan

## Goal

Develop a Jellyfin plugin (initially targeting **Jellyfin 10.11.11**)
that automatically enriches media items with searchable tags derived
from metadata and trusted external sources.

## Design Principles

-   Generic, extensible metadata enrichment.
-   User-configurable tag namespaces.
-   Automatic synchronization on library scans.
-   No manual tagging required.
-   Stable, machine-readable tag values.

## Initial Tag Taxonomy

  Namespace          Example
  ------------------ -------------------------------
  `origin=`          `origin=india`
  `lang=`            `lang=bengali` (original language)
  `audio_lang=`      `audio_lang=hindi` (tracks on the files)
  `year=`            `year=1954`
  `award=`           `award=oscar:best_picture`
  `nomination=`      `nomination=bafta:best_actor`
  `list=`            `list=imdb_top_250`

The `original_lang=` namespace from earlier drafts was folded into `lang=`: the original
language is what a Languages library wants to browse by, and the audio tracks became
their own optional namespace instead.

## MVP (v1.0) — shipped

### Metadata already available

-   Production countries
-   Original language
-   Audio languages (multiple)
-   First release year

### Behaviour

-   Create/update tags automatically.
-   Remove obsolete tags.
-   Configurable prefixes.
-   Process movies and TV shows.

## v1.1 — shipped in 0.1.0

External enrichment, no API keys required:

-   TMDb, through the server's built-in client — origin and original language become
    external-first with Jellyfin metadata as the fallback.
-   TVDb, through the official TheTVDB plugin when installed; TVDb-only series bridge
    through TMDb's find endpoint otherwise.
-   IMDb ids are the join key for the award and list datasets below; IMDb itself exposes
    no metadata API.

## v1.2 — shipped in 0.1.0

Awards:

-   Academy Awards (full history, from DLu/oscar_data)
-   BAFTA, Golden Globes, Primetime Emmys (from Wikidata — partial, winner-heavy)

Winners and nominations are separate namespaces; nominations include the winner so the
nominee set is complete on its own. Ceremonies are selectable individually.

Examples:

-   `award=oscar:best_picture`
-   `nomination=oscar:best_director`

## v1.3 — shipped in 0.1.0

Curated lists, selectable individually, snapshotted into the plugin at release time:

-   IMDb Top 250
-   Sight & Sound 2022 critics' poll
-   BFI Top 100 British films
-   AFI 100 Years…100 Movies (2007)
-   National Film Registry
-   Criterion Collection
-   TSPDT 1,000 Greatest Films

## Architecture

``` text
Metadata Sources
        │
        ▼
Metadata Enrichment
        │
        ▼
Normalization
        │
        ▼
Tag Generation
        │
        ▼
Jellyfin Database
```

## Storage

Use Jellyfin's supported APIs so tags are stored in the Jellyfin
database. Do not edit NFO sidecar files directly.

## Configuration

-   Enable/disable providers
-   Configure namespaces
-   Force full rescan
-   Dry-run mode
-   Logging

## Future Enhancements

-   Rule engine
-   Custom transforms
-   User-defined metadata mappings
-   Prefix/wildcard search helper
-   Scheduled enrichment jobs

## Success Criteria

-   Automatic tagging with no manual maintenance.
-   Fast incremental updates.
-   Extensible provider framework.
-   Backwards-compatible tag schema.
