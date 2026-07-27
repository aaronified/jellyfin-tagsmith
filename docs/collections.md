# Collections projection

**Status: implemented in 0.0.3, off by default. The rendering of a plugin-created
collections library has not yet been confirmed on a live server — enable one namespace
first.**

Jellyfin dropped tags from global search in 10.10, and several clients — Fladder among
them — neither display nor filter on tags at all. Tags are therefore invisible on exactly
the devices most people watch on.

Collections are the fix. They are ordinary library items, so every client can browse them
without knowing tags exist.

## Model

Tags remain the source of truth. Collections are a **projection** of the tag set, rebuilt
on each run. Retag an item and the collections follow. Turn the projection off and the
tags are untouched.

Grouping reads the tags actually on the item, not what a provider would compute, so a tag
added by hand in the Jellyfin UI lands in its collection on the next run exactly like a
generated one. For that to be useful the tag also has to survive, which is why the tag
writer records what it wrote — see *Hand-added tags* in
[tagging.md](tagging.md#hand-added-tags).

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

## Granularity

The projection is not obliged to mirror tag granularity. `year=1954` stays per-year as a
tag, because that precision is what makes filtering useful, but the **year projection
groups by decade** — `1950s`, ten tiles instead of a hundred.

Other namespaces project one collection per value.

## Configuration

Per namespace: **generate collections** on or off. Plus one global **remove collections
when disabled**, default off.

`year` defaults to off even at decade granularity.

## Images

One image per collection. Jellyfin's `Primary` and `Thumb` are single-slot, and while
`Backdrop` accepts several, the UI shows only the first — there is no per-item slideshow.
Animated GIFs are accepted but the server re-encodes on resize, so treat them as static.

Without an image, tiles fall back to a collage of member posters, so this is polish rather
than a requirement.

### Artwork is not bundled

The plugin ships no images. A starter set lives in `assets/thumbnails/<namespace>/` in this
repository — flat flags with a gradient and a label for `origin`, native-script name cards
for `lang`, decade cards for `year` — generated at build time so no fonts or drawing
libraries ever enter the plugin. Users download the set they want, or make their own.

This keeps the installable plugin small, which matters when the update-from-dashboard loop
is the main way it gets tested.

### Where they go

```
<config>/tagsmith/thumbnails/origin/india.png
<config>/tagsmith/thumbnails/lang/bengali.png
<config>/tagsmith/thumbnails/year/1950s.png
```

The filename stem is matched against the tag value after running both through
`TagNormalizer.Slug`. That makes matching case-insensitive and forgiving of separators, so
`india.png`, `India.PNG`, `United States.png` and `united-states.png` all resolve. Accepted
extensions: `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`.

### Precedence

1. A file in the thumbnails folder.
2. An image set by hand in the Jellyfin UI.
3. Nothing — Jellyfin's member collage.

Rule: **Tagsmith writes an image only when the collection has none, or when the current
image is one Tagsmith itself wrote and the source file has changed.** It records a hash of
what it applied. Without this the scheduled task would silently overwrite every poster the
user hand-picked — the same failure shape as resurrecting a deleted library.

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
