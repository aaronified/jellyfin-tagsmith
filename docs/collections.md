# Collections projection

**Status: specification, not yet implemented. Target 0.0.3.**

Jellyfin dropped tags from global search in 10.10, and several clients — Fladder among
them — neither display nor filter on tags at all. Tags are therefore invisible on exactly
the devices most people watch on.

Collections are the fix. They are ordinary library items, so every client can browse them
without knowing tags exist.

## Model

Tags remain the source of truth. Collections are a **projection** of the tag set, rebuilt
on each run. Retag an item and the collections follow. Turn the projection off and the
tags are untouched.

```
tags  ->  group items by tag  ->  reconcile collections  ->  reconcile libraries
```

Nothing is copied, moved, or symlinked. A collection is a database item plus a ~1 KB
`collection.xml` listing the item IDs of its members. Media directories are never read or
written.

## Disk layout

One `boxsets` virtual folder per enabled namespace, each with its own directory under the
Jellyfin config volume:

```
<config>/data/collections/            existing, never touched
<config>/data/tagsmith-origin/
    India [boxset]/collection.xml
    Japan [boxset]/collection.xml
<config>/data/tagsmith-lang/
    Bengali [boxset]/collection.xml
```

Roughly 100 files under 200 KB for a typical library.

**Jellyfin must have write access to its config directory.** On a read-only container
filesystem collection creation fails outright
([jellyfin#14504](https://github.com/jellyfin/jellyfin/issues/14504)). This requirement is
stated on the settings page and in the README.

## What the user sees

A library per namespace — "Origins", "Languages" — on the home screen of every client.
Opening one shows a poster tile per value; opening a tile shows the films. Collections are
named by value (`India`), not by full tag (`origin=india`), since the namespace is already
the library name.

Visibility caveats, none of them plugin-controlled:

- Users without *Enable access to all libraries* need the new library granted explicitly.
- Users can hide libraries from their own home screen.
- Clients cache the library list; a refresh or re-login may be needed.

## Configuration

Per namespace: **generate collections** on or off. Plus one global **remove collections
when disabled**, default off.

`year` defaults to off — one collection per year is a hundred tiles nobody asked for.

## Reconciliation

Tagsmith records the IDs of every library and collection it creates. It only ever modifies
or deletes those. A hand-made collection that happens to share a name is never touched.

On each run:

| Situation | Behaviour |
| --- | --- |
| Namespace enabled, library missing | Create it |
| Namespace enabled, library exists | Reconcile its collections |
| Namespace disabled in Tagsmith | Stop maintaining. Delete only if *remove when disabled* is set |
| **Library deleted in Jellyfin's own settings** | Treat as intent. Flip the namespace off in config, log it, **do not recreate** |
| Collection exists for a value with no items left | Delete it (Tagsmith-owned only) |
| Box sets orphaned by a library deleted out of band | Clean up on the next run |

The fourth row is the one that matters. The failure mode worth designing against is not
deletion — it is a nightly task silently resurrecting a library the user deliberately
removed, leaving them unable to be rid of it short of uninstalling the plugin. Reconcile,
never resurrect.

## Performance

Cost scales with the number of collections, not library size. Per collection per run:
compute the desired member set, diff against current members, write only on difference.
Steady state is a few dozen set comparisons and zero writes — negligible beside the tag
pass, which already issues a media-stream query per item.

The first run is the expensive one: creating every collection, each writing
`collection.xml` and triggering a refresh, on whatever storage the config volume sits on.
Expect minutes.

Elapsed time is logged per phase so this can be measured rather than estimated. The
figures above are a model, not a benchmark.

## Verified API surface

Compiled against `Jellyfin.Controller` 10.11.11:

```csharp
library.AddVirtualFolder(name, CollectionTypeOptions.boxsets, new LibraryOptions(), true);
await library.RemoveVirtualFolder(name, refreshLibrary: true);
library.GetVirtualFolders();

await collections.CreateCollectionAsync(new CollectionCreationOptions { Name, ParentId, ItemIdList, IsLocked });
await collections.AddToCollectionAsync(boxSetId, itemIds);
await collections.RemoveFromCollectionAsync(boxSetId, itemIds);

library.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = false }, notifyParentItem: true);
```

**Unverified:** that a plugin-created `boxsets` virtual folder renders identically to the
built-in Collections library. It compiles; whether clients treat it the same needs a live
test. Ship behind an off-by-default toggle and enable one namespace first.
