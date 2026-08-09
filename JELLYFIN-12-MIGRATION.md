# Jellyfin 12 migration

**Status: investigation only. No code changes until Jellyfin 12 final ships.**

Tagsmith targets 10.11.11 (`build.yaml` `targetAbi: 10.11.11.0`, `Jellyfin.Controller`
10.11.11) and that is not moving yet. This document records what 12 changes, what it does
not, and what has to be decided once 12 is out of pre-release.

Read [docs/collections.md](docs/collections.md) first. Everything here assumes the
architecture described there and does not restate it.

**Provenance of the claims below.** Everything marked VERIFIED was read from Jellyfin and
jellyfin-web source at the pinned tags `v10.11.11` and `v12.0-rc4`, with the two versions
diffed file by file. Nothing here is recalled from memory. Nothing here has been confirmed
by running a 12 server — the empirical tests in §6 are exactly the gap between source
review and knowing.

## The short version

The projection still works on 12rc4, but the thing it exists to provide — a separate shelf
per namespace — is gone. Every Tagsmith library's collections land in one undifferentiated
pile.

The cause is **not** a parentId scoping failure, and the fix is **not** a one-line change.
Both of those were the obvious first reading and both are wrong.

Separately, and more urgently, §5 documents a 12 change that affects collection
**membership** rather than presentation. It is unrelated to the view problem and should not
be bundled with it.

## Confirmed on 12rc4 by hand

Observed directly on a running server, before any source review:

| | |
| --- | --- |
| Underlying data | Intact. Box-set folders resolve, `collection.xml` parses, membership correct |
| Artwork | Works |
| Tags | Unaffected |
| Library GUIDs | Unchanged across the upgrade |

The library tile's link changed shape, same GUID on both sides:

```
10.11.11   list?parentId=eb72be6db5e077fbd8bb04a9fb03b340&serverId=75de2d2f82f141edbab0314235b79a74
12rc4      boxsets?topParentId=eb72be6db5e077fbd8bb04a9fb03b340&collectionType=boxsets
```

Pasting the **old** `list?parentId=…` URL into 12rc4 by hand still works and opens the
correctly scoped library.

## What is actually happening — VERIFIED

`Jellyfin.Api/Controllers/ItemsController.cs` gained a block in 12 that does not exist at
10.11.11 (the 10.11.11 file has no occurrence of `linkedChildAncestorIds`,
`ICollectionFolder` or `CollectionType.boxsets`):

```csharp
if (includeItemTypes.Length == 1
    && (includeItemTypes[0] == BaseItemKind.BoxSet || includeItemTypes[0] == BaseItemKind.Playlist)
    && item is not BoxSet && item is not Playlist)
{
    var itemCollectionType = item is IHasCollectionType hct ? hct.CollectionType : null;
    var targetCollectionType = includeItemTypes[0] == BaseItemKind.BoxSet
        ? CollectionType.boxsets : CollectionType.playlists;
    if (parentId.HasValue && item is not UserRootFolder && itemCollectionType != targetCollectionType)
    {
        linkedChildAncestorIds = [parentId.Value];
    }

    parentId = null;                                  // unconditional
    item = _libraryManager.GetUserRootFolder();       // unconditional
}
```

Any request for `IncludeItemTypes=BoxSet` has its parent scope **discarded** and is
re-rooted at the user root folder. `#/boxsets?topParentId=` produces exactly such a
request, and because the library really is typed `boxsets`,
`itemCollectionType == targetCollectionType`, so the compensating `linkedChildAncestorIds`
is never populated. That is the pile, in full.

**So it is not a scoping bug.** The parent is not being ignored by accident; it is being
deliberately thrown away and replaced.

### Why the hand-typed `list?parentId=` URL still works

`jellyfin-web`'s legacy `list.js` sends `Recursive`/`IncludeItemTypes` only when the route
carries a `type` parameter. A library tile link has none, so the request arrives with
`includeItemTypes` empty, misses the re-rooting block entirely, keeps its `parentId`, and
only then picks up `CollectionType.boxsets => [BaseItemKind.BoxSet]` and `recursive = true`
further down the controller.

That URL works **partly because the library is typed `boxsets`.** This matters enormously
for what follows.

## The CollectionType hypothesis is dead — VERIFIED

The obvious fix was to stop declaring these libraries `boxsets` so the client routes them
through `list?parentId=`. It fails three independent ways, each sufficient:

1. **No enum value reaches that route.** `CollectionTypeOptions` has exactly eight members
   (`movies, tvshows, music, musicvideos, homevideos, boxsets, books, mixed`), and
   `appRouter.js` `getRouteUrl` gives every single one a `topParentId` route. `#/list?parentId=`
   is reached only by falling out of that block entirely. Passing `null` writes no marker
   file and still resolves to `#/mixed`. `mixed` cannot even round-trip — it is written as
   `mixed.collection` and parsed back against `Jellyfin.Data.Enums.CollectionType`, which
   has no `mixed` member.
2. **Retyping makes it worse, not better.** With any non-`boxsets` type,
   `itemCollectionType != targetCollectionType`, so `linkedChildAncestorIds = [libraryId]`
   — and that is a **member-ancestry** filter. Tagsmith's box sets link to the user's real
   films, whose ancestors are the *Movies* library, never ours. It matches nothing. The
   library renders **empty**, which is worse than the pile and reads as data loss.
3. **It would be inert on every existing install.** `LibraryOwnership.Decide` never inspects
   collection type; an existing library matches on `record.ItemId` and returns
   `LibraryAction.Use` → `Adopt`, which never calls `AddVirtualFolder`. The only call site
   is [CollectionProjector.cs:927](Jellyfin.Plugin.Tagsmith/Collections/CollectionProjector.cs#L927).
   Editing line 929 changes behaviour for **new installs only**, silently. A test box where
   the library was recreated would look fixed while every real user stayed broken, with
   nothing in the log.

Point 3 is the trap worth remembering: the one-line change is not merely insufficient, it
is *undetectable* on the box most likely to be used to test it.

## The resolver question is closed, and the answer is safe — VERIFIED

The document previously flagged this as the dangerous unknown. It is neither dangerous nor
unknown.

`BoxSetResolver.cs` is **byte-identical at `v10.11.11` and `v12.0-rc4`** (74 lines). Its
only inputs are `args.IsDirectory`, whether the filename contains `[boxset]`, and
`args.ContainsFileSystemEntryByName("collection.xml")`. The token `CollectionType` does not
appear in the file, and Tagsmith satisfies both triggers independently.

It also wins dispatch under every collection type: resolvers run in `Priority` order and
`BoxSetResolver` inherits `ResolverPriority.First`, ahead of `MovieResolver` (`Fourth`) and
`FolderResolver` (`Last`). Every other `First`-priority resolver declines this folder shape.
`MovieResolver`'s multi-item pre-pass cannot swallow directories — they go to `leftOver` and
are re-resolved normally.

Item identity is stable too: `GetNewItemId(path, type)` is an MD5 over `type.FullName + path`,
so the same path resolving to the same type yields the same GUID. No delete-and-recreate.

`CollectionImageProvider` and `BaseDynamicImageProvider` are likewise byte-identical at both
tags, so the `IsLocked` reasoning in
[BoxSetFolder.cs:141-160](Jellyfin.Plugin.Tagsmith/Collections/BoxSetFolder.cs#L141-L160)
still holds on 12.

**Corollary: resolution and artwork are safe under any collection type.** That entire class
of risk is off the table, which is worth knowing regardless of which option is eventually
taken.

One near-miss recorded for completeness: `PhotoAlbumResolver` matches any directory
containing an image when the type is `homevideos`, and Tagsmith's posters live inside the
`[boxset]` folder. It loses only on priority. **`homevideos` and `photos` are disqualified**
regardless, because `PhotoResolver` would still create a Photo row per collection unless
`EnablePhotos = false` were added to the `LibraryOptions`.

## A separate and more urgent 12 regression — VERIFIED, untested

Unrelated to the view problem. Do not bundle the two.

`MediaBrowser.Providers/BoxSets/BoxSetMetadataService.cs` in 12 wraps the
`collection.xml` → `LinkedChildren` merge in a new guard:

```csharp
if (!string.IsNullOrEmpty(targetPath)
    && !FileSystem.ContainsSubPath(ServerConfigurationManager.ApplicationPaths.DataPath, targetPath))
```

`CollectionProjector.MediaPathFor` is `Path.Combine(_paths.DataPath, "tagsmith-" + kind)`.
**Every Tagsmith box set lives under `DataPath`, so on 12 the merge is skipped.** The dedup
key also moved from `i.Path` to `ItemId ?? Path` — precisely the semantics
[BoxSetFolder.cs:126-132](Jellyfin.Plugin.Tagsmith/Collections/BoxSetFolder.cs#L126-L132)
reasons about, and the reason members are written by path.

An upgraded server looks fine because the rows already exist from 10.11. The exposure is
**newly created collections, membership changes, and any rebuild from scratch**. Test D.

## Options

Ranked by cost. Verified reasoning, unverified outcomes.

1. **Change nothing; file upstream.** The unconditional `parentId = null` for any
   `IncludeItemTypes=BoxSet` request discards a valid scope, and `linkedChildAncestorIds`
   filtering by *member* ancestry makes the compensating branch useless for any collection
   whose members live elsewhere — which is all of them. Both are defensible bug reports, 12
   is still RC, and a fix upstream costs Tagsmith nothing and helps everyone. **This is the
   best first move regardless of what else is done**, because it is the only path that puts
   no user library at risk.
2. **Document the legacy-layout workaround.** The `#/boxsets` branch in `appRouter.js` is
   gated on `isModernLayout`. Switching Display settings to a legacy layout makes 12rc4
   route tiles through `#/list?parentId=` **today**. Zero code, zero risk, user-reversible.
3. **Name-prefix the collections** (`Origins · France`, `Decades · 1970s`). Entirely within
   Tagsmith's own data — no retyping, no library migration. Turns the pile from
   undifferentiated into sorted and grouped, and behaves identically on both versions. This
   is the honest degradation. Note it changes the canonical form of a collection name, so
   every collection folder is renamed once: a breaking change, and it belongs in the release
   notes as one.
4. **`CollectionType.folders` (=12) via a hand-written marker file.** The only surviving
   version of the retype idea. It is not expressible through `AddVirtualFolder`; it means
   deleting `boxsets.collection` and writing `folders.collection` in
   `DefaultUserViewsPath`. It falls through to `#/list?parentId=` on *both* versions, is
   double-covered by the `context !== 'folders'` guard, and is not eligible for view
   grouping. **Known collateral:** the dashboard shows the library with no content type
   (`GetCollectionType` parses the marker as `CollectionTypeOptions`, which has no
   `folders`); `CollectionPosterVerifyPostScanTask` (new in 12) would re-refresh forever on
   a library without Tagsmith artwork; and it invalidates the reasoning at
   [ArtworkSynchronizer.cs:474-478](Jellyfin.Plugin.Tagsmith/Collections/ArtworkSynchronizer.cs#L474-L478),
   which is explicitly scoped to `boxsets`. It also writes outside the
   `<DataPath>/tagsmith-<kind>` fence every other destructive path is gated on, needs its
   own narrow fence, needs a dry-run guard, and needs a `ManagedLibrary` field to record
   "retyped" so the migration does not re-run every sync.
5. **Version-gated behaviour** on `IApplicationHost.ApplicationVersion` — 10.11 keeps
   libraries, 12 falls back to (3). Needs the ABI question answered first and means
   maintaining two presentations.
6. **Not viable:** any `CollectionTypeOptions` value; `mixed`; `null`; `photos` or
   `homevideos`; nesting box sets under an intermediate folder (the re-rooting discards the
   parent at any depth).

## If option 4 is ever taken: do not use `Rebuild`

The retype must happen **in place**. Routing it through `LibraryAction.Rebuild` is
actively destructive, for reasons that are worth fixing on their own merits:

- The marker swap keeps the path, therefore the library GUID, therefore every per-user
  access grant. Grants live in `PreferenceKind.EnabledFolders` and **removal never purges
  them — it orphans them**.
- `Rebuild` recreates using `plan.Name`, not `folder.Name`, so any user who renamed a
  Tagsmith library in the dashboard (a rename `Adopt` deliberately follows) has it reverted
  → new path → new GUID → orphaned grants only a human can restore, per user, by hand.
- `RemoveLibraryAsync` returns without removing on an id miss and the caller ignores the
  outcome, so `AddVirtualFolder`'s dedup counter mints `Origins2` — two libraries on one
  media path, with the record already forgotten so Tagsmith can never clean up the orphan.
- `RemoveVirtualFolder` wraps `Directory.Delete(path, true)` with no catch, and passes
  `refreshLibrary: true`, racing the `AddVirtualFolder` two lines later — the exact race the
  comment at [CollectionProjector.cs:923-926](Jellyfin.Plugin.Tagsmith/Collections/CollectionProjector.cs#L923-L926)
  already warns about.

**Not at risk in any path (verified):** member films, favourites and watch state on
collections, and the artwork trees, which are disjoint from `<DataPath>/tagsmith-<kind>`.

## Test plan for 12 final

Cheapest first. Each step can kill the one after it.

**A. Settle the remaining unknown (2 min, no code, no risk).**
```
GET /Items?ParentId=<L>                                        → expect only that namespace's box sets
GET /Items?ParentId=<L>&IncludeItemTypes=BoxSet&Recursive=true → expect every box set on the server
```
Same parent, different result, purely from `IncludeItemTypes` — confirms the re-rooting and
proves the pile is not a scoping failure. Then run the second call against a `movies`-typed
test library holding a copy of a `tagsmith-<kind>` directory: **0 items** confirms the
empty-library prediction and closes out every `CollectionTypeOptions` value on the merits.

**B. Only if option 4 is on the table.** Scratch `boxsets` library over a *copy* of a
`tagsmith-<kind>` directory. Scan, confirm resolution. Swap the marker file, restart, then
check: does the entry still appear in `GET /Library/VirtualFolders` and with what type; does
`GET /Items?ParentId=<L2>` still return the box sets (**make-or-break**); is the GUID
unchanged; does the tile route to `#/list?parentId=`.

**C. Side effects of `folders`.** Does the tile appear on Home and in the sidebar at all
(`UserViewManager` behaviour here is unknown)? Does artwork survive? Does the marker survive
a Dashboard → Libraries edit-and-save — **this one decides whether the approach is shippable
at all**.

**D. The `DataPath` regression, independently of everything above.** On 12, create a *new*
collection and confirm `GET /Items?ParentId=<newBoxSetId>` returns its members. If empty,
`MergeData` is skipping `collection.xml` and membership on 12 rests on something else.

**E. Free sanity check.** Switch to a legacy layout; tiles should route through
`#/list?parentId=` today.

## Decision

**Wait for 12 final. Change no code.**

The breakage is presentational, the data is safe, resolution and artwork are safe under any
collection type, and 10.11 users are unaffected. The attractive one-line fix is dead, and
the surviving variant is a marker-file rewrite outside Tagsmith's ownership fence with a
schema bump and an in-place migration attached — not something to build against an RC that
has already moved routing once.

The first move when 12 final lands is test A, then a bug report upstream. Not a patch.

Two things to carry forward independently of the view problem: the `DataPath` merge
regression in §5, and hardening `Rebuild` (recreate under `folder.Name`, check the removal
outcome, guard the delete), which is worth doing whether or not any of this is adopted.
