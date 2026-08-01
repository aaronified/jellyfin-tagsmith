# Tagsmith — working notes for Claude

Jellyfin plugin that writes namespaced metadata tags. Target server is pinned in
`build.yaml` (`targetAbi`) and must match the `Jellyfin.Controller` package version in the
csproj, or the plugin loads as `NotSupported`.

## Workflow

After **every** code change, in order:

1. `test` agent (haiku) — builds and runs the suite. Nothing proceeds on a FAIL.
2. `validate` agent (opus) — before a release, or after any change to pruning, the
   country dictionary, or anything that writes to the library database.
3. `commit-message` agent (sonnet) — writes the commit message from the diff.
4. `release-notes` agent (sonnet) — before running the Release workflow, from all commits
   since the last tag.

## Commands

```bash
dotnet build Jellyfin.Plugin.Tagsmith -c Release
dotnet test tests/Jellyfin.Plugin.Tagsmith.Tests
# If the machine lacks the .NET 9 runtime, tests run with:
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/Jellyfin.Plugin.Tagsmith.Tests

# Regenerate the embedded datasets (needs Node; all but countries hit the network)
npm --prefix scripts install
node scripts/generate-countries.mjs
node scripts/generate-awards.mjs
node scripts/generate-lists.mjs
```

## Ground rules

- **The library database is the user's data.** Tagsmith only ever touches tags whose
  prefix it owns (`KnownPrefixes`). Never widen that without a migration story, and never
  write during a dry run.
- **Tag values are a schema.** Changing the canonical form of a value rewrites every
  affected tag on the next run. That is a breaking change and belongs in the release notes.
- **Verify Jellyfin APIs by compiling, not from memory.** The SDK is available and builds
  take seconds. The API surface shifts between server versions.
- **The country dictionary is generated, not hand-edited.** Change
  `scripts/country-aliases-extra.json` or the generator and regenerate. Never edit
  `Data/countries.json.gz`.
- **Collection artwork is hand-maintained, not generated.** There is no artwork generator
  any more; `assets/` is a set of files, edited directly. Do not write one that rebuilds
  `assets/thumbnails/<namespace>/` from scratch — the posters there are the only copy.

## Layout

| Path | What it is |
| --- | --- |
| `Jellyfin.Plugin.Tagsmith/Tagging/` | Normaliser, alias map, country catalog, synchroniser |
| `Jellyfin.Plugin.Tagsmith/Providers/` | Tag sources implementing `ITagProvider` |
| `Jellyfin.Plugin.Tagsmith/ScheduledTasks/` | The library-wide sync task |
| `scripts/` | CLDR dictionary generator, manifest updater |
| `tests/` | xunit suite |
| `docs/plan.md` | Roadmap |
| `docs/tagging.md` | Tag lifecycle and guarantees |
