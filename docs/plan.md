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
  `lang=`            `lang=bengali`
  `original_lang=`   `original_lang=japanese`
  `year=`            `year=1954`
  `award=`           `award=oscar:best_picture`
  `nomination=`      `nomination=bafta:best_actor`
  `list=`            `list=imdb_top_250`

## MVP (v1.0)

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

## v1.1

Support external enrichment:

-   TMDb
-   TVDb
-   IMDb

User supplies API keys where required.

## v1.2

Awards:

-   Academy Awards
-   BAFTA
-   Golden Globes
-   Primetime Emmys

Store winners and nominations separately.

Examples:

-   `award=oscar:best_picture`
-   `nomination=oscar:best_director`

## v1.3

Curated lists:

-   IMDb Top 250
-   Sight & Sound
-   BFI Top 100
-   AFI 100
-   National Film Registry
-   Criterion Collection
-   TSPDT

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
