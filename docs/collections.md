# Collections projection

**Status: implemented in 0.0.3, revised in 0.0.4, 0.0.5 and 0.0.6, off by default. The
rendering of a plugin-created collections library has not yet been confirmed on a live
server — enable one namespace first.**

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
generated one — and is maintained the same way afterwards. See *Ownership is by namespace*
in [tagging.md](tagging.md).

```
tags  ->  group items by tag  ->  reconcile collections  ->  reconcile libraries
```

Nothing is copied, moved, or symlinked. A collection is a database item plus a ~1 KB
`collection.xml` listing its members. Media directories are never read or written.

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

### How a collection is created

Tagsmith writes the box set folder itself rather than calling
`ICollectionManager.CreateCollectionAsync`. That method takes a
`CollectionCreationOptions.ParentId`, but **10.11.11 ignores it** — it resolves its parent
with `GetCollectionsFolder(true)`, hard-wired to `Path.Combine(appPaths.DataPath,
"collections")`. Every collection therefore landed in the user's built-in Collections
library while the libraries Tagsmith created stayed empty. A box set belongs to whichever
library its folder sits in, so the only way to place one is to create the directory there.

The on-disk contract, all of it transcribed from the 10.11.11 server source:

| Piece | Rule | Where it comes from |
| --- | --- | --- |
| Folder name | `<Display Name> [boxset]` | `BoxSetResolver` resolves a directory as a box set when the name contains `[boxset]` **or** it holds a `collection.xml`, then strips the suffix to derive the name |
| Metadata file | `collection.xml` in that folder | `BoxSetXmlSaver.GetLocalSavePath`, `BoxSetXmlProvider.GetXmlFile` |
| Root element | `<Item>` | `BaseXmlSaver.GetRootElementName`, not overridden for box sets |
| Members | `<CollectionItems><CollectionItem>…` | `BoxSetXmlParser.FetchFromCollectionItemsNode` |
| Member reference | `<Path>` when the item has one, `<ItemId>` otherwise | `LinkedChild.Create`, and `BaseItemXmlParser.GetLinkedChild` |
| Lock | `<LockData>true</LockData>` | see [Images](#images) |

Members are referenced **by path**, not by id, and that is not a style choice.
`BoxSetMetadataService.MergeData` merges linked children with `.DistinctBy(i => i.Path)`,
so a file whose members all carry a null path collapses to a single member.

Getting this wrong produces a folder that resolves as a plain media folder, which is worse
than producing nothing at all, so `BoxSetFolderTests` pins each rule to the server
behaviour it mirrors.

After writing the folders for a projection, Tagsmith runs one scoped `ValidateChildren`
over its own directory — the same call the full library scan makes, just rooted lower down
— so the collections appear in the same run rather than the next one. The user's media
folders are never scanned.

### How membership is maintained afterwards

Writing the file is a one-off. From then on Tagsmith uses
`ICollectionManager.AddToCollectionAsync` and `RemoveFromCollectionAsync`, which take an
existing box set id and are the same calls the web UI makes. Two reasons:

- `collection.xml` is only read at scan time, so rewriting it would leave the database
  stale until something triggered a rescan — every membership change would cost a scan and
  land at an unpredictable moment.
- Once the collection exists the file is Jellyfin's. The library is created with
  `SaveLocalMetadata = true` and `AddToCollectionAsync` queues a refresh with `ForceSave`,
  so `BoxSetXmlSaver` rewrites it. Writing it ourselves would race the saver, and the saver
  would win.

## Dry run

*Dry run* covers the projection, not just tagging. With it on, Tagsmith creates no
libraries, no collections and no files, deletes nothing, neither applies nor adopts artwork,
and does not write configuration — including the projection-disabled decision below. It logs
each action it would have taken, prefixed `[dry-run]`. The adoption listener honours it too,
so a poster set while dry run is on is logged and not copied.

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

Tagsmith's collections are created **locked** (`<LockData>true</LockData>`). That is what
keeps Jellyfin's remote image providers off them —
`ProviderManager.CanRefreshImages` returns false for a locked item outside a forced full
refresh. Without it a provider-supplied poster is indistinguishable from one the user set
by hand, and the adoption rule below would write it over the user's curated artwork file.
Locking also keeps remote *metadata* providers off, which is what you want for an item
called "India".

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

### The folder is the source of truth, in both directions

Artwork moves both ways, but never in the same operation. Three triggers, one job each:

| Trigger | What it does about artwork |
| --- | --- |
| **Sync Tagsmith tags** (scheduled, nightly at 04:00) | Applies the file in the thumbnails folder to the collections **it created in that run**, and to nothing else. Never adopts |
| **A poster changing on a collection** (event) | Adopts it into the thumbnails folder immediately |
| **Reapply collection artwork** (button) | Forces the folder onto **every** collection Tagsmith owns, discarding posters set by hand |

Set a poster by hand on a collection in the library UI and Tagsmith **adopts** it: the
image is copied into `<config>/tagsmith/thumbnails/<namespace>/` as the stored artwork for
that value, replacing whatever was there. From then on it is an ordinary file you can back
up, edit or delete, and it survives the collection being rebuilt or the library being torn
down and recreated.

Adoption is driven by `ILibraryManager.ItemUpdated`, so it happens the moment the poster is
set rather than at the next nightly run. That matters both ways round: the backup is
immediate, and the heavy library-wide pass no longer has to hash a poster per collection to
find out whether anything changed. The listener ignores every item whose id is not in
`ManagedCollections`, which is nearly all of them.

Once a collection exists, the nightly run does not touch its artwork at all. Change the
image in the thumbnails folder and nothing happens until you press **Reapply collection
artwork**; that is the only trigger that reads the folder for a collection that already
exists.

The table above is `ArtworkPolicy.Decide`, one function, so the "no overlap" claim is a
test rather than a reading of three call sites.

#### Why it cannot loop

Applying artwork writes to the collection, and writing to the collection is exactly the
thing the listener watches for. Two guards, and both have to fail:

- Adoption **only ever writes into the thumbnails folder**. It never touches the collection,
  so it raises no item update and cannot re-trigger itself. The two directions cannot
  ping-pong; at most one image update exists per apply.
- Each direction is guarded by a hash, so a run with nothing changed writes nothing. Two
  hashes are kept per value, not one: the artwork file in the thumbnails folder, and the
  image that actually landed on the collection. They differ because the server re-encodes on
  save, and conflating them meant Tagsmith read its own poster as user intent and copied it
  back over the source file.

The hash of what landed is only recorded *after* the save returns, so there is a window in
which Tagsmith's own poster is on the collection and the record does not yet say so. The
projector marks the box set id before it writes anything and unmarks it in the same step
that records the hash, both under the lock the listener reads and decides under. So the
listener sees one of three states and never a state in between: before the apply, where the
poster it adopts really is the user's; during it, where the mark is present and it stands
off; or after it, where the hash says the poster is Tagsmith's. The lock is not held across
the image save itself, so nothing can wedge if a future server version raises the event from
inside it.

That is also why the handler does its work inline on Jellyfin's thread rather than queueing
it: work resumed on a worker would run after the lock had been released and the mark
dropped, which is exactly the state the guard exists to exclude.

One value never accumulates two files: adopting `india.jpg` removes an existing
`india.png`.

## Reconciliation

Tagsmith records the **id** of every library and collection it creates, and only ever
modifies or deletes those. Nothing is ever claimed by name: a library or a hand-made
collection that happens to share a name is never touched, and never deleted.

On each run:

| Situation | Behaviour |
| --- | --- |
| Namespace enabled, library missing | Create it |
| Namespace enabled, library exists | Reconcile its collections |
| Collection created in this run | Apply its artwork from the thumbnails folder |
| Collection already existed | Reconcile membership only. Its artwork is not read, written or hashed — see [Images](#images) |
| **Wanted library name already taken by a library Tagsmith does not own** | Refuse. Log an error and skip the projection — never adopt |
| Library renamed in Jellyfin's own settings | Follow the rename. Ownership is by id, so this is not a deletion |
| Library name changed in *Tagsmith's* settings | Tear down and rebuild — Jellyfin 10.11 exposes no rename on `ILibraryManager` |
| Namespace disabled in Tagsmith | Stop maintaining. Delete only if *remove when disabled* is set |
| **Library deleted in Jellyfin's own settings** | Treat as intent. Flip the namespace off in config, **persist that immediately**, log it, **do not recreate** |
| Collection exists for a value with no items left | Delete it (Tagsmith-owned only) |
| Box sets orphaned by a library deleted out of band | Delete their folders in the same pass. Their database items went with the library, so this is a guarded directory delete: the folder must end in ` [boxset]` and sit directly inside one of Tagsmith's own per-projection directories |

The library-deleted row is the one that matters. The failure mode worth designing against
is not deletion — it is a nightly task silently resurrecting a library the user
deliberately removed, leaving them unable to be rid of it short of uninstalling the plugin.
Reconcile, never resurrect. That decision is written to configuration the moment it is
made, rather than at the end of the run, so cancelling the task partway through cannot lose
it.

Two secondary signals keep this honest. The configured library name is sanitised exactly as
`LibraryManager.AddVirtualFolder` sanitises it — `GetValidFilename` on a trimmed name —
before it is compared with anything, because otherwise a name like `Origins/Countries`
never matches the `Origins Countries` Jellyfin actually created, and the nightly task
produces `Origins Countries2`, `Origins Countries3`, one library per run. And each
projection's library points at a private directory, so a library serving
`<config>/data/tagsmith-origin` is Tagsmith's by construction — that is what heals a lost
record and what migrates a pre-0.0.5 configuration, which recorded only a name.

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
// Awaited. AddVirtualFolder returns a Task, and the await ValidateTopLibraryFolders in its
// finally block is what registers the CollectionFolder — fire and forget leaves ItemId null
// on the very next lookup, and swallows the ArgumentException for a bad path as an
// unobserved task exception.
await library.AddVirtualFolder(name, CollectionTypeOptions.boxsets, options, refreshLibrary: false);
await library.RemoveVirtualFolder(name, refreshLibrary: true);
library.GetVirtualFolders();              // VirtualFolderInfo: Name, ItemId, Locations

library.FindByPath(path, isFolder: true);
await folder.ValidateChildren(progress, new MetadataRefreshOptions(new DirectoryService(fs)),
                              recursive: true, allowRemoveRoot: false, cancellationToken);

await collections.AddToCollectionAsync(boxSetId, itemIds);
await collections.RemoveFromCollectionAsync(boxSetId, itemIds);

library.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true }, notifyParentItem: true);

// Adoption. ItemChangeEventArgs carries Item, Parent and UpdateReason. Subscribed and
// unsubscribed by an IHostedService registered in PluginServiceRegistrator; plugin
// registrators feed the generic host's service collection, so AddHostedService is started
// and stopped with the server.
library.ItemUpdated += (sender, e) => { … };

// ItemUpdateType is [Flags] and one update carries several reasons, so this is a mask. Note
// the server declares None = 1, not 0.
(e.UpdateReason & ItemUpdateType.ImageUpdate) != 0
```

`LibraryManager.UpdateItemsAsync` invokes `ItemUpdated` inline, on the calling thread, after
`SaveItems`. The handler therefore runs inside whatever operation caused the change, which is
why the adoption handler does its work synchronously and stays small: queueing it to a worker
would let it outlive the projector's "currently applying" marker, which is the thing that
stops it copying Tagsmith's own poster back over the user's file.

**`ProviderManager.SaveImage(item, stream, …)` does not write to the database.** In 10.11.11
`ImageSaver.SaveImage` writes the file and calls `BaseItem.SetImagePath`, which mutates
`ImageInfos` in memory and nothing else; `ImageController.SetItemImage` follows it with
`item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, …)` and that is what both persists
the image and raises the event. Tagsmith does not make that second call, so applied artwork is
not persisted and Tagsmith's own applies do not raise `ItemUpdated` directly — though a
refresh queued elsewhere still can, which is why the loop guard does not depend on that.

**`CollectionCreationOptions.ParentId` compiles but is ignored.** Do not reintroduce it.
`CollectionManager.CreateCollectionAsync` never reads it:

```csharp
var folderName = _fileSystem.GetValidFilename(name) + " [boxset]";
var parentFolder = await GetCollectionsFolder(true).ConfigureAwait(false);   // <data>/collections
var path = Path.Combine(parentFolder.Path, folderName);
```

Every collection therefore goes to the built-in Collections library whatever `ParentId`
says. That is why Tagsmith writes the folder itself; `ICollectionManager` is used only for
membership on a collection that already exists.

Two settings on the created library are load-bearing:

- `SaveLocalMetadata = true` — without it Jellyfin never writes `collection.xml` back, so
  the membership Tagsmith seeded has no on-disk mirror. `CollectionManager` sets it for the
  built-in Collections library; `new LibraryOptions()` defaults it to false.
- `EnableRealtimeMonitor = false` — same reason `CollectionManager` sets it: nothing here is
  edited from outside.

**Unverified:** that a plugin-created `boxsets` virtual folder renders identically to the
built-in Collections library. It compiles; whether clients treat it the same needs a live
test. Ship behind an off-by-default toggle and enable one namespace first.
