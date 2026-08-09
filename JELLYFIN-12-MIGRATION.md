# Jellyfin 12 migration

**Status: investigation only. No code changes until Jellyfin 12 final ships.**

Tagsmith targets 10.11.11 (`build.yaml` `targetAbi: 10.11.11.0`, `Jellyfin.Controller`
10.11.11) and that is not moving yet. This document records what 12rc4 changes, what it
does not, and what has to be decided once 12 is out of pre-release. It is a holding
document — it will be wrong in places by the time 12 final lands, and the point is to know
*which* places.

Read [docs/collections.md](docs/collections.md) first. Everything here assumes the
architecture described there; nothing here restates it.

## The short version

The collections projection still *works* on 12rc4. What breaks is the one thing it exists
to provide: a separate shelf per namespace. In 12 the web client drops every Tagsmith
library's collections into a single undifferentiated view — which is precisely the pile the
virtual-library design was built to escape.

Nothing is wrong with the data. Everything is wrong with the presentation.

## Confirmed on 12rc4

Observed directly on a running 12rc4 server. These are facts, not inferences.

| | |
| --- | --- |
| Underlying data | Intact. Box-set folders resolve, `collection.xml` parses, membership is correct |
| Artwork | Works |
| Tags | Unaffected |
| Library GUIDs | Unchanged across the upgrade |
| The defect | Purely the view the web client opens for a Tagsmith library tile |

The library tile's link changed shape, with the **same library GUID** on both sides:

```
10.11.11   list?parentId=eb72be6db5e077fbd8bb04a9fb03b340&serverId=75de2d2f82f141edbab0314235b79a74
12rc4      boxsets?topParentId=eb72be6db5e077fbd8bb04a9fb03b340&collectionType=boxsets
```

The new `boxsets` route does not scope to the parent: every namespace's collections appear
together.

**The decisive observation:** pasting the *old* `list?parentId=…` URL into 12rc4 by hand
works perfectly and opens that specific library, correctly scoped.

That single fact rules out most of the frightening explanations. It establishes that on 12:

- the server still scopes by `parentId` correctly;
- the `list` route still exists and still works in the web client;
- the box sets still resolve and still display;
- **the only defect is which route the web client generates for the library tile.**

This is a client-side routing regression, not a data-model change. It is worth being
precise about that, because the fixes appropriate to a broken view are much cheaper — and
much less destructive — than the fixes appropriate to a broken data model.

## The lever we have

The web client selects that route from the library's **`CollectionType`**, and Tagsmith
sets it, in one place:

```csharp
// Collections/CollectionProjector.cs:927
await _libraryManager.AddVirtualFolder(
        plan.Name,
        CollectionTypeOptions.boxsets,   // <- this
        new LibraryOptions { … },
        false)
```

So the leading hypothesis is: **declare the virtual folder as something other than
`boxsets`**, so the 12 client routes its tile through `list?parentId=` — the route already
proven to work — instead of `boxsets?topParentId=`.

`CollectionTypeOptions`, from the 10.11.11 SDK, in full:

```
books  boxsets  homevideos  mixed  movies  music  musicvideos  tvshows
```

There is no `unset`/`none` member, so "no collection type" would mean passing `null`, if
the parameter is nullable. Unverified.

### Why this might not work

Do not treat the one-line change as safe until these are answered against real source. The
failure mode is not cosmetic — it is *every collection on a user's server silently ceasing
to be a collection*.

1. **Resolver gating (the dangerous one).** Does a `<Name> [boxset]` directory containing
   `collection.xml` still resolve as a `BoxSet` when it sits in a library whose
   `CollectionType` is *not* `boxsets`? If `MovieResolver` or the generic folder resolver
   claims the directory first in a `movies`-typed library, the change trades a bad view for
   no collections at all — and per docs/collections.md, a folder that resolves as a plain
   media folder is worse than producing nothing.
2. **Migration cost.** `ILibraryManager` exposes no way to change an existing virtual
   folder's `CollectionType` in place, as far as is currently known — which would make this
   a `RemoveVirtualFolder` + `AddVirtualFolder`, i.e. the existing `LibraryAction.Rebuild`
   path. That destroys the library row, and with it per-user access grants and any item ids
   keyed on it. Ownership records in `ManagedRecords` do not currently store the collection
   type, so "created under the old type" is not even detectable without a schema bump.
3. **Artwork and lock semantics.** The whole artwork pipeline hangs off `IsLocked`,
   `ProviderManager.CanRefreshImages` and `CollectionImageProvider.Supports`. Whether
   library `CollectionType` perturbs any of that is unverified.
4. **The empty-directory rule.** `.tagsmith-library` exists because
   `IsLibraryFolderAccessible` hard-codes an exemption only for a folder named
   `collections`. Whether 12 changed that check at all is unverified, and it is the failure
   that produces a visible library showing nothing, with no error in the log.

## Open questions for 12 final

Nothing here should be decided against an rc.

- Is the `boxsets` view ignoring `topParentId` a **regression** or a deliberate redesign?
  If upstream fixes it before 12 final, the correct action is to change nothing at all.
  This is the single most important question and it is the cheapest to answer — watch the
  jellyfin-web tracker.
- Does `CollectionManager.CreateCollectionAsync` honour `CollectionCreationOptions.ParentId`
  in 12? If it does, the entire hand-rolled box-set-folder contract could eventually be
  retired in favour of the supported API. That is a large simplification and a large
  rewrite; it is not a 12.0 project.
- What `targetAbi` do 12 plugins declare, and is there a published `Jellyfin.Controller`
  for 12?
- Can one DLL serve both 10.11 and 12, or does this need separate branches? Tagsmith has
  never shipped two builds and doing so has its own cost.
- Do `AddVirtualFolder`, `GetVirtualFolders`, `VirtualFolderInfo`, `ValidateTopLibraryFolders`
  and `CollectionTypeOptions` keep their 10.11 signatures?

## Test plan, when 12 final lands

Against a real instance, in this order — each step is cheap and each one can kill the plan:

1. Install the current 10.11-built plugin on 12 final. Confirm the plugin loads at all
   (`targetAbi` mismatch shows up as `NotSupported`, with no other symptom).
2. Confirm the regression still exists on final, and re-confirm the hand-typed
   `list?parentId=…` URL still works. If either has changed, most of this document is moot.
3. Create a library by hand in the 12 dashboard with `CollectionType` = movies (or mixed),
   drop a `Foo [boxset]/collection.xml` into it, scan, and see whether it resolves as a
   collection. **This is the resolver-gating question, answered empirically in ten minutes,
   with no plugin build and no risk to anything.** Do this before writing a line of code.
4. Only if step 3 passes: check what route the 12 client generates for that hand-made
   library's tile, and whether the view scopes.
5. Only then consider the code change, and the migration path for libraries already
   created as `boxsets`.

Step 3 is the whole ballgame. It is also the step that requires no code, which is why this
document exists instead of a patch.

## Decision

**Wait for 12 final.** Do not change `CollectionTypeOptions.boxsets`, do not bump
`targetAbi`, do not add a migration, and do not ship a 12-compatible build against an rc.

The reasoning: the breakage is presentational, the data is safe, 10.11 users are entirely
unaffected, and the most likely fix costs one line — but only if the resolver question
comes back clean, and that cannot be settled against a moving target. An rc that changes
routing once can change it twice. Anything shipped now risks a teardown-and-rebuild
migration, executed on the user's library, in service of a bug that upstream may fix before
release.
