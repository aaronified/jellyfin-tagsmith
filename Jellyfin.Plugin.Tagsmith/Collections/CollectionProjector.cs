using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Tagsmith.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Projects the tag set into browsable collection libraries, for the many clients that
/// neither display nor filter on tags.
/// </summary>
/// <remarks>
/// <para>
/// Tags stay the source of truth; collections are rebuilt from them. Tagsmith records the
/// id of everything it creates and only ever modifies or deletes those, so a library or
/// collection that merely shares a name is never touched.
/// </para>
/// <para>
/// This class creates and deletes libraries, collections and files. Every path that writes
/// is gated on <see cref="PluginConfiguration.DryRun"/>.
/// </para>
/// </remarks>
public class CollectionProjector
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ArtworkSynchronizer _artwork;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<CollectionProjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionProjector"/> class.
    /// </summary>
    public CollectionProjector(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ArtworkSynchronizer artwork,
        IFileSystem fileSystem,
        IApplicationPaths paths,
        ILogger<CollectionProjector> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _artwork = artwork;
        _fileSystem = fileSystem;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles every projection against the current tag set.
    /// </summary>
    public async Task ProjectAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var run = new Run
        {
            Configuration = configuration,
            DryRun = configuration.DryRun,
            Pass = new ArtworkPass
            {
                Thumbnails = _artwork.ResolveThumbnails(),
                DryRun = configuration.DryRun
            },

            // The heavy pass applies artwork wherever that cannot destroy user intent: the
            // collections it creates, anything with no poster at all, and its own posters
            // gone stale because the source file changed. It never adopts: a poster set in
            // the library UI is captured the moment it is set, by PosterAdoptionListener,
            // rather than waiting for the next full sync.
            Artwork = ArtworkMode.ScheduledRun
        };

        if (run.DryRun)
        {
            _logger.LogInformation("Tagsmith: [dry-run] projecting collections; nothing will be written");
        }

        // Every cancellation point used to skip the save at the end. Most of the state
        // self-heals on the next run, but one decision does not: a library the user deleted
        // in Jellyfin's own settings flips its projection off in memory, and losing that
        // means the next run recreates the library they deliberately removed.
        // Every enabled projection's directory, before any of them is projected. The single
        // revalidation below fires during whichever projection first needs it, and it can
        // only create a physical folder row for a directory that is non-empty at that
        // instant — so seeding per projection, inside its own pass, would leave the ones
        // later in the enum order unhealable by it and escalate them to a full media scan.
        if (!run.DryRun)
        {
            foreach (var kind in Enum.GetValues<ProjectionKind>().Where(k => WillBuild(k, configuration)))
            {
                var path = MediaPathFor(kind);
                try
                {
                    SeedMediaDirectory(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Tagsmith: could not seed {Path}", path);
                }
            }
        }

        try
        {
            foreach (var kind in Enum.GetValues<ProjectionKind>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TagGrouping.IsEnabled(kind, configuration))
                {
                    await TearDownAsync(kind, run, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await ProjectKindAsync(kind, items, run, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Persist(run);
        }
    }

    /// <summary>
    /// Applies the artwork in the thumbnails folder to every collection Tagsmith owns,
    /// replacing whatever poster is on them.
    /// </summary>
    /// <remarks>
    /// A poster set in the library UI is adopted into the thumbnails folder as soon as it is
    /// set. This is the escape hatch for when that is not what you wanted — after dropping in
    /// a new set of images, or after an adoption you would rather undo. It reads the folder
    /// only; collections with no matching file keep what they have.
    /// </remarks>
    public async Task ReapplyArtworkAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Fully synchronous work, but async so cancellation and faults surface on the
        // returned task rather than being thrown from the method itself.
        await Task.Yield();

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var run = new Run
        {
            Configuration = configuration,
            DryRun = configuration.DryRun,
            Pass = new ArtworkPass
            {
                Thumbnails = _artwork.ResolveThumbnails(),
                DryRun = configuration.DryRun
            },
            Artwork = ArtworkMode.ReapplyFromFolder
        };

        var records = configuration.ManagedCollections;
        var libraries = configuration.ManagedLibraries;
        var total = records.Length + libraries.Length;

        if (total == 0)
        {
            _logger.LogInformation("Tagsmith: no projected collections to apply artwork to");
            return;
        }

        _logger.LogInformation(
            "Tagsmith: reapplying artwork from {Folder} to {Count} collections and {Libraries} libraries",
            run.Pass.Thumbnails.Root,
            records.Length,
            libraries.Length);

        try
        {
            var done = 0;

            // The library tiles first, then every collection. Both go through the same
            // policy; ReapplyFromFolder forces the folder onto whatever poster is there.
            foreach (var library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncLibraryArtwork(library, createdThisRun: false, run);
                progress?.Report(100.0 * ++done / total);
            }

            var unresolved = 0;

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Resolve by recorded id, then by the folder Tagsmith wrote. A record whose
                // id never got recorded — the projection ran before its library folder
                // existed — is still a real collection on disk, and this button is exactly
                // what a user reaches for when its poster is wrong. Recovery records the id
                // it found, so an installation in that state needs the fallback once rather
                // than on every press.
                var boxSet = Resolve(record) ?? RecoverBoxSet(record, run);

                if (boxSet is null)
                {
                    unresolved++;
                }
                else
                {
                    SyncCollectionArtwork(record, boxSet, createdThisRun: false, run);
                }

                progress?.Report(100.0 * ++done / total);
            }

            if (unresolved > 0)
            {
                // Silence here used to read as success while nothing had been touched.
                _logger.LogWarning(
                    "Tagsmith: {Count} of {Total} recorded collections could not be found and kept their artwork; run the sync task first",
                    unresolved,
                    records.Length);
            }
        }
        finally
        {
            Persist(run);
        }
    }

    /// <summary>
    /// Runs one collection's artwork through the policy for this trigger.
    /// </summary>
    private void SyncCollectionArtwork(ManagedCollection record, BaseItem boxSet, bool createdThisRun, Run run)
    {
        var tagNamespace = TagGrouping.NamespaceFor(record.Kind, run.Configuration);
        if (string.IsNullOrWhiteSpace(tagNamespace))
        {
            return;
        }

        _artwork.Sync(run.Artwork, ArtworkTarget.Collection(boxSet, record, tagNamespace), createdThisRun, run.Pass);
    }

    /// <summary>
    /// Runs one projection library's tile artwork through the policy for this trigger. The
    /// tile file lives at the root of the thumbnails tree, named after the namespace —
    /// <c>thumbnails/origin.png</c> for the Origins library.
    /// </summary>
    private void SyncLibraryArtwork(ManagedLibrary record, bool createdThisRun, Run run)
    {
        var tagNamespace = TagGrouping.NamespaceFor(record.Kind, run.Configuration);
        if (string.IsNullOrWhiteSpace(tagNamespace) || !Guid.TryParse(record.ItemId, out var id))
        {
            return;
        }

        // The CollectionFolder is the item the dashboard itself puts library images on;
        // anything else under that id is not a library Tagsmith should touch.
        if (_libraryManager.GetItemById(id) is not CollectionFolder folder)
        {
            return;
        }

        _artwork.Sync(run.Artwork, ArtworkTarget.Library(folder, record, tagNamespace), createdThisRun, run.Pass);
    }

    private async Task ProjectKindAsync(
        ProjectionKind kind,
        IReadOnlyList<BaseItem> items,
        Run run,
        CancellationToken cancellationToken)
    {
        var configuration = run.Configuration;

        var tagNamespace = TagGrouping.NamespaceFor(kind, configuration);
        if (string.IsNullOrWhiteSpace(tagNamespace))
        {
            _logger.LogWarning("Tagsmith: the {Kind} projection is on but its namespace is blank; skipping", kind);
            return;
        }

        // Two projections reading one namespace build two identical libraries, and — because
        // artwork is keyed on the namespace, not the kind — they then fight over one tile
        // file and one set of posters, each adopting over the other every run. Easy to do by
        // accident with award and nomination, whose values are the same shape. The kind
        // declared first keeps the namespace; the later one stands off rather than both
        // thrashing.
        var claimedBy = Enum.GetValues<ProjectionKind>()
            .Where(other => other < kind
                            && TagGrouping.IsEnabled(other, configuration)
                            && string.Equals(
                                TagGrouping.NamespaceFor(other, configuration),
                                tagNamespace,
                                StringComparison.OrdinalIgnoreCase))
            .Cast<ProjectionKind?>()
            .FirstOrDefault();

        if (claimedBy is { } owner)
        {
            _logger.LogError(
                "Tagsmith: the {Kind} projection reads {Namespace}{Separator}, which the {Other} projection already claims; skipping it, "
                + "because two libraries built from one namespace hold the same collections and overwrite each other's artwork. "
                + "Give them different namespaces",
                kind,
                tagNamespace,
                configuration.Separator,
                owner);
            return;
        }

        var wanted = GroupItems(kind, items, tagNamespace, configuration.Separator);

        // Zero values is never a reason to act. Two different things produce it and neither
        // wants what the reconciliation below would do:
        //
        //   Nothing built yet — creating the library would put an empty shelf on every
        //   client's home screen. The usual cause is a projection whose tagging is switched
        //   off, and award, nomination and list tagging all default off, so ticking only the
        //   projection is an easy mistake to make.
        //
        //   Already built — falling through would reach RemoveEmptyAsync, which would find
        //   every record stale and delete every collection and its folder. But "no tags"
        //   here is absence of evidence: an embedded dataset that failed to load, a ceremony
        //   unticked, an external source down. Tagsmith already refuses to rewrite tags on
        //   that basis (see the lookup-failed handling in CoreMetadataTagProvider) and must
        //   not demolish a library on it either. Tearing a projection down is the
        //   RemoveCollectionsWhenDisabled decision, or the delete task, never this.
        //
        // Collections for values that disappear individually are still pruned; only the
        // all-gone case is treated as suspect.
        if (wanted.Count == 0)
        {
            var built = configuration.ManagedLibraries.Any(l => l.Kind == kind);

            if (!TagGrouping.SourceIsTagged(kind, configuration))
            {
                _logger.LogWarning(
                    "Tagsmith: the {Kind} projection is on but nothing is writing {Namespace}{Separator} tags, so there is nothing to project; "
                    + "switch that tagging on as well, and its ceremonies or lists if it has any. {Existing}",
                    kind,
                    tagNamespace,
                    configuration.Separator,
                    built ? "Its existing collections are left as they are." : "No library was created.");
            }
            else
            {
                _logger.LogInformation(
                    "Tagsmith: no {Namespace}{Separator} tags in the library, so the {Kind} projection has nothing to reconcile. {Existing}",
                    tagNamespace,
                    configuration.Separator,
                    kind,
                    built ? "Its existing collections are left as they are." : "No library was created.");
            }

            return;
        }

        var library = await EnsureLibraryAsync(kind, run, cancellationToken).ConfigureAwait(false);
        if (library is null)
        {
            return;
        }

        // The library's own home-screen tile, before its collections: the tile is the first
        // thing the user sees, and it follows exactly the same policy table the collection
        // posters do.
        if (run.Configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind) is { } libraryRecord)
        {
            SyncLibraryArtwork(libraryRecord, library.Value.Created, run);
        }

        PruneDuplicateRecords(kind, run);

        var pending = new List<PendingCollection>();

        foreach (var (value, members) in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = FindRecord(kind, value, run);
            var boxSet = Resolve(record) ?? RecoverBoxSet(record, run);

            if (boxSet is not null && !SitsIn(boxSet, library.Value))
            {
                // Written by a version that used CreateCollectionAsync, so it is sitting in
                // the user's built-in Collections library — ParentId was ignored. Move it by
                // deleting and rewriting it in the right place; the membership is rebuilt
                // from the tags either way.
                _logger.LogInformation(
                    "Tagsmith: collection {Name} is in the wrong library; recreating it under {Path}",
                    boxSet.Name,
                    library.Value.MediaPath);

                DeleteCollection(record!, run);
                boxSet = null;
            }

            if (boxSet is not null)
            {
                // Membership, then artwork through the same policy as everything else. The
                // policy applies the folder to an existing collection only when it has no
                // poster or when Tagsmith's own poster went stale because the file changed —
                // a poster the user set by hand is never touched here. This is what makes
                // "drop images into the folder, run a sync" work for collections that
                // already existed, which is how the folder is documented to behave.
                await SyncMembersAsync(boxSet, members, run, cancellationToken).ConfigureAwait(false);
                SyncCollectionArtwork(record!, boxSet, createdThisRun: false, run);
                continue;
            }

            var path = CreateBoxSetFolder(kind, value, members, library.Value, run, ref record);
            if (path is not null && record is not null)
            {
                pending.Add(new PendingCollection(record, path));
            }
        }

        if (pending.Count > 0)
        {
            await ResolveNewCollectionsAsync(library.Value, pending, run, cancellationToken)
                .ConfigureAwait(false);
        }

        await RemoveEmptyAsync(kind, wanted.Keys, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups items by projected value, reading the tags on the item so hand-added tags
    /// are honoured alongside generated ones.
    /// </summary>
    private static Dictionary<string, List<CollectionMember>> GroupItems(
        ProjectionKind kind,
        IReadOnlyList<BaseItem> items,
        string tagNamespace,
        string separator)
    {
        var grouped = new Dictionary<string, List<CollectionMember>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            foreach (var tag in item.Tags)
            {
                var value = TagGrouping.ValueFor(kind, tag, tagNamespace, separator);
                if (value is null)
                {
                    continue;
                }

                if (!grouped.TryGetValue(value, out var members))
                {
                    grouped[value] = members = [];
                    seen[value] = [];
                }

                if (seen[value].Add(item.Id))
                {
                    members.Add(new CollectionMember(item.Id, item.Path));
                }
            }
        }

        return grouped;
    }

    // ---------------------------------------------------------------- membership

    /// <summary>
    /// Brings an existing collection's membership in line with the tags.
    /// </summary>
    /// <remarks>
    /// Membership is maintained through <see cref="ICollectionManager"/> rather than by
    /// rewriting <c>collection.xml</c>, even though Tagsmith wrote that file to create the
    /// collection in the first place. Two reasons, both decisive:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// The XML is only read at scan time. Rewriting it would leave the database stale until
    /// something triggered a rescan of the folder, so every membership change would cost a
    /// scan and take effect at an unpredictable moment.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Once the collection exists, Jellyfin's own <c>BoxSetXmlSaver</c> owns that file — the
    /// library is created with <c>SaveLocalMetadata = true</c>, and
    /// <c>AddToCollectionAsync</c> queues a refresh with <c>ForceSave</c>. Writing it
    /// ourselves would race the saver, and the saver would win.
    /// </description>
    /// </item>
    /// </list>
    /// So: Tagsmith writes the file once, to bring the box set into existence in the right
    /// library; from then on it uses the same API the web UI uses, and the file is Jellyfin's.
    /// </remarks>
    private async Task SyncMembersAsync(
        BaseItem boxSet,
        List<CollectionMember> wanted,
        Run run,
        CancellationToken cancellationToken)
    {
        // Collection membership is held as linked children, not as real parenting.
        // Enumerating them here also populates each LinkedChild's cached ItemId, which is
        // one of the two things RemoveFromCollectionAsync matches on.
        var current = boxSet is Folder folder
            ? folder.GetLinkedChildren().Select(c => c.Id)
            : [];

        var change = MemberDiff.Between(current, wanted.Select(m => m.Id));
        if (change.IsEmpty)
        {
            return;
        }

        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] {Name} would change by +{Added} -{Removed}",
                boxSet.Name,
                change.Add.Count,
                change.Remove.Count);
            return;
        }

        if (change.Add.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(boxSet.Id, change.Add).ConfigureAwait(false);
        }

        if (change.Remove.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, change.Remove).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Tagsmith: {Name} +{Added} -{Removed}",
            boxSet.Name,
            change.Add.Count,
            change.Remove.Count);

        cancellationToken.ThrowIfCancellationRequested();
    }

    // ---------------------------------------------------------------- creation

    /// <summary>
    /// Writes the box set folder for a value into the projection's library, and returns the
    /// path so the caller can resolve it once the scan has run. Returns null when nothing
    /// was written.
    /// </summary>
    /// <remarks>
    /// <c>ICollectionManager.CreateCollectionAsync</c> is deliberately not used.
    /// <c>CollectionCreationOptions.ParentId</c> compiles but is ignored in 10.11.11 — the
    /// method always calls <c>GetCollectionsFolder(true)</c>, which is hard-wired to
    /// <c>&lt;data&gt;/collections</c> — so every collection landed in the user's built-in
    /// Collections library while the libraries Tagsmith created stayed empty.
    /// </remarks>
    private string? CreateBoxSetFolder(
        ProjectionKind kind,
        string value,
        List<CollectionMember> members,
        LibraryHandle library,
        Run run,
        ref ManagedCollection? record)
    {
        var displayName = TagGrouping.DisplayName(kind, value);

        var folderName = BoxSetFolder.FolderNameFor(displayName);
        if (folderName is null)
        {
            _logger.LogWarning(
                "Tagsmith: the value {Value} produces no usable folder name; skipping its collection",
                value);
            return null;
        }

        var path = Path.Combine(library.MediaPath, folderName);

        // Two different values could in principle sanitise to the same folder. Both would
        // then resolve to one box set and fight over its membership every run.
        var clash = run.Configuration.ManagedCollections.FirstOrDefault(c =>
            c.Kind == kind
            && !string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

        if (clash is not null)
        {
            _logger.LogWarning(
                "Tagsmith: {Value} and {Other} both want the folder {Path}; skipping {Value}",
                value,
                clash.Value,
                path,
                value);
            return null;
        }

        var xml = BoxSetFolder.BuildMetadata(displayName, members, locked: true);

        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would create collection {Name} with {Count} items at {Path}",
                displayName,
                members.Count,
                path);
            return null;
        }

        try
        {
            Directory.CreateDirectory(path);

            var metadata = Path.Combine(path, BoxSetFolder.MetadataFileName);
            if (!File.Exists(metadata) || !string.Equals(File.ReadAllText(metadata), xml, StringComparison.Ordinal))
            {
                File.WriteAllText(metadata, xml);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Jellyfin needs write access to its config volume; on a read-only container
            // filesystem this is where it fails. jellyfin#14504.
            _logger.LogError(ex, "Tagsmith: could not write the collection folder {Path}", path);
            return null;
        }

        // Reuse the existing record rather than appending a second one for the same value.
        // Appending is what used to leave a dead record behind that RemoveEmptyAsync, which
        // matches on Value alone, could never prune.
        if (record is null)
        {
            record = new ManagedCollection { Kind = kind, Value = value };
            run.Configuration.ManagedCollections = [.. run.Configuration.ManagedCollections, record];
        }

        record.Id = string.Empty;
        record.Path = path;

        // Both hashes describe artwork Tagsmith put on a box set that no longer exists — the
        // folder just written resolves to a new one with no poster at all. Leaving them would
        // make the apply below decide the artwork was already there and skip it, so a
        // recreated collection would come back blank and stay blank, since nothing after this
        // run looks at its artwork again.
        record.ImageHash = string.Empty;
        record.AppliedImageHash = string.Empty;
        run.Dirty = true;

        return path;
    }

    /// <summary>
    /// Gets Jellyfin to resolve the box set folders just written, then records their ids.
    /// </summary>
    /// <remarks>
    /// One scan for the whole projection rather than one per collection. The scan is scoped
    /// to Tagsmith's own directory — the same <c>ValidateChildren</c> call the full library
    /// scan makes, just rooted lower down — so it never touches the user's media folders.
    /// </remarks>
    private async Task ResolveNewCollectionsAsync(
        LibraryHandle library,
        List<PendingCollection> pending,
        Run run,
        CancellationToken cancellationToken)
    {
        var mediaFolder = await ResolveMediaFolderAsync(library, run, cancellationToken).ConfigureAwait(false);
        if (mediaFolder is null)
        {
            // Last resort, and once per run: a full scan resolves every projection's folders,
            // so queueing it per kind buys nothing. Their membership is already in the files
            // we wrote.
            if (!run.DryRun && !run.QueuedLibraryScan && !_libraryManager.IsScanRunning)
            {
                run.QueuedLibraryScan = true;
                _logger.LogWarning(
                    "Tagsmith: {Path} is still not a library folder Jellyfin knows about; queueing a full library scan to pick up {Count} new collections",
                    library.MediaPath,
                    pending.Count);
                _libraryManager.QueueLibraryScan();
            }

            return;
        }

        await mediaFolder.ValidateChildren(
                new Progress<double>(),
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                recursive: true,
                allowRemoveRoot: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FindBoxSet(entry.Path) is not { } boxSet)
            {
                _logger.LogWarning(
                    "Tagsmith: wrote {Path} but Jellyfin has not resolved it as a collection yet; it will be picked up on the next run",
                    entry.Path);
                continue;
            }

            entry.Record.Id = boxSet.Id.ToString("N");
            run.Dirty = true;

            _logger.LogInformation("Tagsmith: created collection {Name} at {Path}", boxSet.Name, entry.Path);

            SyncCollectionArtwork(entry.Record, boxSet, createdThisRun: true, run);
        }
    }

    /// <summary>
    /// Resolves the <see cref="Folder"/> item behind a projection's media directory, healing
    /// a library whose physical folder Jellyfin never created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the item everything hangs off: a <c>CollectionFolder</c>'s own
    /// <c>ValidateChildrenInternal</c> is a no-op, so the physical folder is the only thing
    /// that can actually resolve <c>&lt;value&gt; [boxset]</c> directories into box sets, and
    /// its id is what <c>PhysicalFolderIds</c> — and therefore every client query against the
    /// library — is made of. See <see cref="SeedMediaDirectory"/> for how it goes missing.
    /// </para>
    /// <para>
    /// The repair is <c>ValidateTopLibraryFolders</c>, the same call <c>AddVirtualFolder</c>
    /// makes. It is what recovers a projection created by an earlier version against an empty
    /// directory — there are box set folders in there now, so the row Jellyfin refused to
    /// create the first time is created on this pass and the library stops rendering empty.
    /// </para>
    /// <para>
    /// <b>It is a write, not a lookup.</b> It refreshes the two root folders and every
    /// library, and it validates the physical root with removal enabled — <c>shouldRemove</c>
    /// is <c>!IsRoot</c>, and only <c>UserRootFolder</c> ever sets <c>IsRoot</c> — so a
    /// library folder that is empty or unreachable at that instant, a network mount that
    /// happens to be down, has its rows deleted. That is strictly gentler than the
    /// <c>QueueLibraryScan</c> it replaces, which does the same thing and then scans all the
    /// user's media, but it is not free: hence once per run, never while a scan is already
    /// running, and never during a dry run.
    /// </para>
    /// <para>
    /// Not <c>ValidateMediaLibrary</c>, and not <c>AddVirtualFolder(refreshLibrary: true)</c>:
    /// both resolve to <c>CancelIfRunningAndQueue&lt;RefreshMediaLibraryTask&gt;</c>, which
    /// aborts a library scan the user may be part-way through. <c>QueueLibraryScan</c> only
    /// queues, which is why it is still usable as the last resort.
    /// </para>
    /// </remarks>
    private async Task<Folder?> ResolveMediaFolderAsync(LibraryHandle library, Run run, CancellationToken cancellationToken)
    {
        if (FindMediaFolder(library.MediaPath) is { } resolved)
        {
            return resolved;
        }

        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] {Path} is not a library folder Jellyfin knows about; would revalidate the library roots",
                library.MediaPath);
            return null;
        }

        if (_libraryManager.IsScanRunning)
        {
            // Not marked as spent: nothing was revalidated, and the scan may well be over by
            // the time the next projection asks.
            _logger.LogInformation(
                "Tagsmith: {Path} is not a library folder Jellyfin knows about, and a library scan is already running; leaving it to that scan",
                library.MediaPath);
            return null;
        }

        if (run.RevalidatedLibraryRoots)
        {
            // One pass covers every library, so a second would find exactly what the first
            // did. Logged rather than silent: a projection that resolves nothing is the
            // failure shape this whole release exists to make visible.
            _logger.LogWarning(
                "Tagsmith: {Path} did not resolve even after this run revalidated the library roots",
                library.MediaPath);
            return null;
        }

        _logger.LogInformation(
            "Tagsmith: {Path} is not a library folder Jellyfin knows about; revalidating the library roots",
            library.MediaPath);

        run.RevalidatedLibraryRoots = true;
        await _libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);

        if (FindMediaFolder(library.MediaPath) is not { } repaired)
        {
            return null;
        }

        _logger.LogInformation("Tagsmith: {Path} resolved after revalidation", library.MediaPath);
        return repaired;
    }

    /// <summary>
    /// The plain <see cref="Folder"/> at a projection's media path, if Jellyfin has one.
    /// </summary>
    /// <remarks>
    /// Excludes <see cref="CollectionFolder"/> explicitly. Validating one of those does
    /// nothing at all — its <c>ValidateChildrenInternal</c> returns a completed task — so
    /// accepting one here would look like success and resolve no collections.
    /// </remarks>
    private Folder? FindMediaFolder(string mediaPath) =>
        _libraryManager.FindByPath(mediaPath, true) is Folder folder and not CollectionFolder ? folder : null;

    /// <summary>
    /// Finds the box set Jellyfin resolved for a folder Tagsmith wrote.
    /// </summary>
    /// <remarks>
    /// By id first. Item ids are a pure function of path and type — <c>AddChild</c> and the
    /// resolver both derive them with <c>GetNewItemId</c> — so this is an exact keyed lookup.
    /// <c>FindByPath</c> is not: it is a <c>Limit 1</c> query ordered by <c>DateCreated</c>
    /// descending with no type filter, so a stale row of another type at the same path wins
    /// and the box set is reported missing. It stays as the fallback because a database
    /// written with case-sensitive ids, or by a version that stored a differently normalised
    /// path, will not answer to the computed id.
    /// </remarks>
    private BoxSet? FindBoxSet(string path) =>
        _libraryManager.GetItemById(_libraryManager.GetNewItemId(path, typeof(BoxSet))) as BoxSet
        ?? _libraryManager.FindByPath(path, true) as BoxSet;

    // ---------------------------------------------------------------- libraries

    /// <summary>
    /// Resolves the projection's library, creating it if Tagsmith is allowed to.
    /// </summary>
    private async Task<LibraryHandle?> EnsureLibraryAsync(ProjectionKind kind, Run run, CancellationToken cancellationToken)
    {
        var configuration = run.Configuration;
        var mediaPath = MediaPathFor(kind);
        var record = configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind);

        var plan = LibraryOwnership.Decide(
            record,
            TagGrouping.LibraryNameFor(kind, configuration),
            mediaPath,
            Survey());

        switch (plan.Action)
        {
            case LibraryAction.Invalid:
                _logger.LogError(
                    "Tagsmith: the {Kind} projection has no usable library name; set one on the settings page",
                    kind);
                return null;

            case LibraryAction.NameConflict:
                _logger.LogError(
                    "Tagsmith: a library called {Name} already exists and Tagsmith did not create it. Refusing to adopt it — "
                    + "rename the {Kind} projection's library in Tagsmith's settings, or remove the existing library first",
                    plan.Name,
                    kind);
                return null;

            case LibraryAction.Abandoned:
                await AbandonAsync(kind, record!, run, cancellationToken).ConfigureAwait(false);
                return null;

            case LibraryAction.Rebuild:
                _logger.LogInformation(
                    "Tagsmith: the {Kind} library name changed from {Old} to {New}; rebuilding, since Jellyfin 10.11 has no rename",
                    kind,
                    record!.ConfiguredName,
                    plan.Name);

                if (run.DryRun)
                {
                    _logger.LogInformation("Tagsmith: [dry-run] would tear down and rebuild the {Kind} library", kind);
                    return null;
                }

                await DeleteCollectionsAsync(kind, run, cancellationToken).ConfigureAwait(false);
                await RemoveLibraryAsync(record, run).ConfigureAwait(false);
                Forget(kind, run);
                record = null;
                break;

            case LibraryAction.Use:
                return Adopt(kind, plan, mediaPath, run);
        }

        // Create.
        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would create the library {Name} at {Path}",
                plan.Name,
                mediaPath);
            return null;
        }

        try
        {
            // Before AddVirtualFolder, never after: an empty library folder is skipped
            // outright. See SeedMediaDirectory.
            SeedMediaDirectory(mediaPath);

            // Awaited, unlike 0.0.4. AddVirtualFolder registers the CollectionFolder in its
            // finally block, via ValidateTopLibraryFolders — fire and forget meant ItemId was
            // still null on the lookup two lines later, so the first run created the library
            // and zero collections. It also meant the ArgumentException for a bad path was
            // swallowed as an unobserved task exception.
            //
            // refreshLibrary: false because Tagsmith runs its own scoped scan below; a full
            // background scan here would race it. It is also destructive — it resolves to
            // ValidateMediaLibrary, which is CancelIfRunningAndQueue and would abort a scan
            // the user is running.
            await _libraryManager.AddVirtualFolder(
                    plan.Name,
                    CollectionTypeOptions.boxsets,
                    new LibraryOptions
                    {
                        PathInfos = [new MediaPathInfo(mediaPath)],
                        EnableRealtimeMonitor = false,

                        // Without this Jellyfin never writes collection.xml back, so the
                        // membership Tagsmith seeded would have no on-disk mirror. This is
                        // what CollectionManager sets for the built-in Collections library.
                        SaveLocalMetadata = true
                    },
                    false)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Tagsmith: could not create the library {Name} at {Path}", plan.Name, mediaPath);
            return null;
        }

        // Located by media path, not by name: AddVirtualFolder sanitises the name and appends
        // a counter while the directory exists, so what it created may not be what we asked
        // for. The media path is ours alone and is exact.
        var created = Survey().FirstOrDefault(f => LibraryOwnership.PointsAt(f, mediaPath));
        if (created.ItemId is null || !Guid.TryParse(created.ItemId, out _))
        {
            _logger.LogError(
                "Tagsmith: created the library {Name} but Jellyfin did not report it back; it will be retried on the next run",
                plan.Name);
            return null;
        }

        _logger.LogInformation("Tagsmith: created library {Name} at {Path}", created.Name, mediaPath);

        configuration.ManagedLibraries =
        [
            .. configuration.ManagedLibraries,
            new ManagedLibrary
            {
                Kind = kind,
                Name = created.Name ?? plan.Name,
                ConfiguredName = plan.Name,
                ItemId = created.ItemId
            }
        ];
        run.Dirty = true;

        return new LibraryHandle(created.ItemId, created.Name ?? plan.Name, mediaPath, Created: true);
    }

    private LibraryHandle? Adopt(ProjectionKind kind, LibraryPlan plan, string mediaPath, Run run)
    {
        var folder = plan.Folder!.Value;
        if (folder.ItemId is null)
        {
            return null;
        }

        // Seeded here as well as in the pre-run pass, because a library can be adopted whose
        // directory that pass skipped — reclaiming one Tagsmith has no record of, for
        // instance. An empty directory is what makes Jellyfin discard the library, so the
        // guarantee has to hold on every path that hands back a usable handle, not just the
        // one that creates it. A File.Exists in the steady state.
        if (!run.DryRun)
        {
            try
            {
                SeedMediaDirectory(mediaPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Tagsmith: could not seed {Path}", mediaPath);
            }
        }

        var record = run.Configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind);

        if (record is not null && !string.Equals(record.Name, folder.Name, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(record.Name))
        {
            // Following a rename made in Dashboard → Libraries. Matching on the recorded id
            // rather than the name is what stops a rename reading as a deletion and silently
            // disabling the projection.
            _logger.LogInformation(
                "Tagsmith: the {Kind} library was renamed to {New} in Jellyfin; following the rename",
                kind,
                folder.Name);
        }

        // Ownership is by id, so a library keeps being Tagsmith's after the user repoints it
        // in Dashboard → Libraries — but then nothing serves the directory Tagsmith writes
        // box sets into, and every run writes folders nobody will ever scan. Say so; the
        // alternative reading, that the projection should be abandoned, would throw away a
        // library over a settings change the user may be halfway through making.
        if (!LibraryOwnership.PointsAt(folder, mediaPath))
        {
            _logger.LogError(
                "Tagsmith: the {Kind} library {Name} no longer serves {Path}; its collections cannot appear until that folder is added back to the library in Jellyfin's settings",
                kind,
                folder.Name,
                mediaPath);
        }

        // A dry run records nothing, not even bookkeeping.
        if (run.DryRun)
        {
            return new LibraryHandle(folder.ItemId, folder.Name ?? plan.Name, mediaPath);
        }

        if (record is null)
        {
            record = new ManagedLibrary { Kind = kind };
            run.Configuration.ManagedLibraries = [.. run.Configuration.ManagedLibraries, record];
            run.Dirty = true;
            _logger.LogInformation(
                "Tagsmith: reclaiming the library {Name} at {Path}, which points at Tagsmith's own directory",
                folder.Name,
                mediaPath);
        }

        if (!string.Equals(record.Name, folder.Name, StringComparison.Ordinal)
            || !string.Equals(record.ItemId, folder.ItemId, StringComparison.Ordinal)
            || !string.Equals(record.ConfiguredName, plan.Name, StringComparison.Ordinal))
        {
            record.Name = folder.Name ?? string.Empty;
            record.ItemId = folder.ItemId;
            record.ConfiguredName = plan.Name;
            run.Dirty = true;
        }

        return new LibraryHandle(folder.ItemId, folder.Name ?? plan.Name, mediaPath);
    }

    /// <summary>
    /// The user deleted one of Tagsmith's libraries in Jellyfin's own settings. Treat that as
    /// intent to disable rather than something to undo, clean up what is left, and persist
    /// the decision straight away.
    /// </summary>
    /// <remarks>
    /// The failure worth designing against is a nightly task silently resurrecting a library
    /// the user removed, leaving them unable to be rid of it short of uninstalling the plugin.
    /// </remarks>
    private async Task AbandonAsync(ProjectionKind kind, ManagedLibrary record, Run run, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Tagsmith: library {Library} was removed outside the plugin; disabling the {Kind} projection rather than recreating it",
            record.Name,
            kind);

        if (run.DryRun)
        {
            _logger.LogInformation("Tagsmith: [dry-run] would disable the {Kind} projection and clean up its box sets", kind);
            return;
        }

        // The box sets outlive the library: removing a virtual folder takes the library
        // definition away but leaves Tagsmith's directory on disk. Forgetting the records
        // without deleting anything, as 0.0.4 did, left them unowned and unreachable forever.
        await DeleteCollectionsAsync(kind, run, cancellationToken).ConfigureAwait(false);
        SweepBoxSetFolders(kind);
        RemoveMediaDirectory(kind);

        TagGrouping.SetEnabled(kind, run.Configuration, false);
        Forget(kind, run);
        Persist(run);
    }

    private async Task RemoveLibraryAsync(ManagedLibrary record, Run run)
    {
        if (run.DryRun)
        {
            _logger.LogInformation("Tagsmith: [dry-run] would remove the library {Name}", record.Name);
            return;
        }

        // Look the library up by the recorded id and use whatever Jellyfin currently calls
        // it. RemoveVirtualFolder takes a name, and removing by a stale name could destroy
        // somebody else's library.
        var folder = Survey().FirstOrDefault(f =>
            string.Equals(f.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase));

        if (folder.ItemId is null || string.IsNullOrEmpty(folder.Name))
        {
            _logger.LogWarning(
                "Tagsmith: not removing the library {Name}; it no longer matches the id Tagsmith recorded",
                record.Name);
            return;
        }

        await _libraryManager.RemoveVirtualFolder(folder.Name, true).ConfigureAwait(false);
        _logger.LogInformation("Tagsmith: removed the library {Name}", folder.Name);
    }

    private async Task TearDownAsync(ProjectionKind kind, Run run, CancellationToken cancellationToken)
    {
        if (!run.Configuration.RemoveCollectionsWhenDisabled)
        {
            return;
        }

        await TearDownKindAsync(kind, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes everything one projection created: its collections, its library and its
    /// box-set folders. <b>Media is never touched.</b> Collections are database items plus
    /// a <c>collection.xml</c> folder under Tagsmith's own per-projection directory —
    /// members are linked children, so deleting a box set deletes the link, not the film —
    /// and removing a virtual folder removes the library definition, not the files it
    /// pointed at. Tags are not touched either; if the projection stays enabled, the next
    /// sync rebuilds everything from them.
    /// </summary>
    private async Task TearDownKindAsync(ProjectionKind kind, Run run, CancellationToken cancellationToken)
    {
        var configuration = run.Configuration;

        var owned = configuration.ManagedCollections.Count(c => c.Kind == kind);
        var library = configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind);
        if (owned == 0 && library is null)
        {
            return;
        }

        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would tear down the {Kind} projection — {Count} collections and its library",
                kind,
                owned);
            return;
        }

        await DeleteCollectionsAsync(kind, run, cancellationToken).ConfigureAwait(false);

        if (library is not null)
        {
            await RemoveLibraryAsync(library, run).ConfigureAwait(false);
        }

        try
        {
            SweepBoxSetFolders(kind);
            RemoveMediaDirectory(kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The library is already gone by this point. Failing here must not skip the
            // Forget below — a stale library record reads as "deleted outside the plugin"
            // on the next sync, which silently disables the projection the user meant to
            // keep. Leftover folders are re-swept by the next teardown.
            _logger.LogWarning(ex, "Tagsmith: could not sweep the {Kind} projection's folders", kind);
        }

        Forget(kind, run);
        _logger.LogInformation("Tagsmith: tore down the {Kind} projection", kind);
    }

    /// <summary>
    /// The <em>Delete projected collections</em> action: tears down every projection —
    /// collections, libraries, box-set folders and the bookkeeping — whatever the
    /// <c>RemoveCollectionsWhenDisabled</c> setting says. Media files and items are never
    /// touched (see <see cref="TearDownKindAsync"/>), and tags are left exactly as they
    /// are, so projections that remain enabled rebuild on the next sync.
    /// </summary>
    public async Task TearDownAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var run = new Run
        {
            Configuration = configuration,
            DryRun = configuration.DryRun,
            Pass = new ArtworkPass
            {
                Thumbnails = _artwork.ResolveThumbnails(),
                DryRun = configuration.DryRun
            },

            // The teardown never calls the artwork synchroniser at all; the mode is inert
            // and set only because Run requires one.
            Artwork = ArtworkMode.AdoptOnly
        };

        _logger.LogInformation(
            "Tagsmith: {Prefix}deleting all projected collections — {Collections} collections across {Libraries} libraries",
            run.DryRun ? "[dry-run] " : string.Empty,
            configuration.ManagedCollections.Length,
            configuration.ManagedLibraries.Length);

        try
        {
            var kinds = Enum.GetValues<ProjectionKind>();

            for (var i = 0; i < kinds.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TearDownKindAsync(kinds[i], run, cancellationToken).ConfigureAwait(false);
                progress?.Report(100.0 * (i + 1) / kinds.Length);
            }
        }
        finally
        {
            Persist(run);
        }
    }

    // ---------------------------------------------------------------- collections

    /// <summary>
    /// Whether a box set actually lives in the projection's library. A box set belongs to
    /// whichever library its folder sits in, so this is the only thing that decides it.
    /// </summary>
    private static bool SitsIn(BoxSet boxSet, LibraryHandle library)
    {
        if (string.IsNullOrEmpty(boxSet.Path))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(boxSet.Path));
        return parent is not null
               && string.Equals(parent, Path.GetFullPath(library.MediaPath), StringComparison.OrdinalIgnoreCase);
    }

    private ManagedCollection? FindRecord(ProjectionKind kind, string value, Run run) =>
        run.Configuration.ManagedCollections.FirstOrDefault(c =>
            c.Kind == kind && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a record to the box set it names, or null.
    /// </summary>
    /// <remarks>
    /// <c>TryParse</c>, not <c>Parse</c>: a hand-edited configuration used to throw a
    /// <see cref="FormatException"/> out of the whole projection here, while the delete path
    /// a hundred lines down was already guarded. And the result must be a
    /// <see cref="BoxSet"/> — an id that resolves to something else is not a collection
    /// Tagsmith made, and the caller goes on to hand it to
    /// <c>DeleteItem(… DeleteFileLocation: true)</c>.
    /// </remarks>
    private BoxSet? Resolve(ManagedCollection? record)
    {
        if (record is null || !Guid.TryParse(record.Id, out var id) || id.Equals(Guid.Empty))
        {
            return null;
        }

        return _libraryManager.GetItemById(id) as BoxSet;
    }

    /// <summary>
    /// Recovers the box set behind a record whose id was never recorded, and records it.
    /// </summary>
    /// <remarks>
    /// A record keeps its folder path even when id resolution failed — which is what happened
    /// to every collection created while its library's physical folder was missing, since
    /// <see cref="ResolveNewCollectionsAsync"/> returned before reaching the id. Without this
    /// the collection is treated as new on every run: its <c>collection.xml</c> is rewritten,
    /// its membership is never reconciled through <c>ICollectionManager</c>, and both the
    /// adoption listener and the reapply action skip it, because all three are keyed on the
    /// id.
    /// </remarks>
    private BoxSet? RecoverBoxSet(ManagedCollection? record, Run run)
    {
        // The path is checked before it is trusted, for the same reason Resolve refuses to
        // trust a recorded id: the caller hands what comes back to DeleteCollection when it
        // fails SitsIn, and that deletes the database item and, inside the data directory,
        // its folder. No version of Tagsmith writes a Path outside the projection's own
        // directory, but a hand-edited or half-restored configuration file can, and recovery
        // must not be the way that becomes a delete.
        if (record is null || string.IsNullOrEmpty(record.Path) || !IsOwnedBoxSetFolder(record.Path))
        {
            return null;
        }

        if (FindBoxSet(record.Path) is not { } boxSet)
        {
            return null;
        }

        _logger.LogInformation(
            "Tagsmith: {Prefix}recovered the id of the collection {Name} from {Path}",
            run.DryRun ? "[dry-run] " : string.Empty,
            boxSet.Name,
            record.Path);

        if (!run.DryRun)
        {
            record.Id = boxSet.Id.ToString("N");
            run.Dirty = true;
        }

        return boxSet;
    }

    /// <summary>
    /// Collapses more than one record for the same value down to one.
    /// </summary>
    private void PruneDuplicateRecords(ProjectionKind kind, Run run)
    {
        var duplicates = run.Configuration.ManagedCollections
            .Where(c => c.Kind == kind)
            .GroupBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToArray();

        if (duplicates.Length == 0)
        {
            return;
        }

        var drop = new List<ManagedCollection>();

        foreach (var group in duplicates)
        {
            var records = group.ToArray();
            var keep = records.FirstOrDefault(r => Resolve(r) is not null) ?? records[0];

            foreach (var record in records)
            {
                if (!ReferenceEquals(record, keep))
                {
                    drop.Add(record);
                }
            }

            _logger.LogWarning(
                "Tagsmith: {Count} records for {Kind}/{Value}; keeping one and discarding the rest",
                records.Length,
                kind,
                group.Key);
        }

        if (run.DryRun)
        {
            return;
        }

        run.Configuration.ManagedCollections =
            run.Configuration.ManagedCollections.Except(drop).ToArray();
        run.Dirty = true;
    }

    private async Task RemoveEmptyAsync(
        ProjectionKind kind,
        IEnumerable<string> stillWanted,
        Run run,
        CancellationToken cancellationToken)
    {
        var wanted = stillWanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = run.Configuration.ManagedCollections
            .Where(c => c.Kind == kind && !wanted.Contains(c.Value))
            .ToArray();

        if (stale.Length == 0)
        {
            return;
        }

        foreach (var record in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCollection(record, run);
        }

        if (!run.DryRun)
        {
            run.Configuration.ManagedCollections =
                run.Configuration.ManagedCollections.Except(stale).ToArray();
            run.Dirty = true;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task DeleteCollectionsAsync(ProjectionKind kind, Run run, CancellationToken cancellationToken)
    {
        foreach (var record in run.Configuration.ManagedCollections.Where(c => c.Kind == kind).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCollection(record, run);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void DeleteCollection(ManagedCollection record, Run run)
    {
        if (run.DryRun)
        {
            _logger.LogInformation("Tagsmith: [dry-run] would delete the collection for {Value}", record.Value);
            return;
        }

        if (Resolve(record) is { } boxSet)
        {
            // DeleteFileLocation: true. The box set folder is Tagsmith's own creation under
            // its own directory, and leaving it behind would make the collection reappear on
            // the next scan.
            //
            // Fenced on the path all the same: a box set outside the server's data
            // directory is a "legacy" box set to Jellyfin, whose real, on-disk children
            // would be enumerated and recursively deleted with it. No record Tagsmith
            // writes can point at one, but this is the single call that deletes files and
            // database rows in one stroke, and it must not trust a hand-edited or restored
            // configuration file. Media must never be deletable from here.
            var deleteFiles = !string.IsNullOrEmpty(boxSet.Path)
                              && Path.GetFullPath(boxSet.Path)
                                  .StartsWith(
                                      Path.GetFullPath(_paths.DataPath) + Path.DirectorySeparatorChar,
                                      StringComparison.OrdinalIgnoreCase);

            if (!deleteFiles)
            {
                _logger.LogWarning(
                    "Tagsmith: the collection {Name} sits outside the data directory at {Path}; "
                    + "removing the database item only and leaving the folder alone",
                    boxSet.Name,
                    boxSet.Path);
            }

            _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = deleteFiles }, true);
            return;
        }

        // The database item is gone — most likely with the library it lived in — but the
        // folder is still on disk and would resolve again.
        DeleteOrphanedFolder(record.Path);
    }

    /// <summary>
    /// Removes a box set folder Tagsmith wrote, after checking it really is one.
    /// </summary>
    private void DeleteOrphanedFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !IsOwnedBoxSetFolder(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
            _logger.LogInformation("Tagsmith: removed the orphaned collection folder {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Tagsmith: could not remove the orphaned collection folder {Path}", path);
        }
    }

    /// <summary>
    /// True only for a <c>… [boxset]</c> directory sitting directly inside one of Tagsmith's
    /// own per-projection directories. Recursive deletes get a belt and braces.
    /// </summary>
    private bool IsOwnedBoxSetFolder(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!Path.GetFileName(full).EndsWith(BoxSetFolder.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(full);
        return parent is not null
               && Enum.GetValues<ProjectionKind>()
                   .Any(k => string.Equals(Path.GetFullPath(MediaPathFor(k)), parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Removes every box set folder left in a projection's own directory.
    /// </summary>
    /// <remarks>
    /// Belt to the braces of the per-record cleanup. A record written before 0.0.5 has no
    /// path, so once its database item has gone with the library there is nothing left to
    /// name the folder — but the directory is Tagsmith's alone, so everything ending in
    /// <c> [boxset]</c> inside it is Tagsmith's too.
    /// </remarks>
    private void SweepBoxSetFolders(ProjectionKind kind)
    {
        var root = MediaPathFor(kind);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*" + BoxSetFolder.Suffix))
        {
            DeleteOrphanedFolder(directory);
        }
    }

    private void RemoveMediaDirectory(ProjectionKind kind)
    {
        var path = MediaPathFor(kind);

        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            if (!MediaDirectory.IsDisposable(path))
            {
                _logger.LogInformation("Tagsmith: leaving {Path} in place; it is not empty", path);
                return;
            }

            // Remove the marker, then delete non-recursively. A recursive delete would close
            // the same window by removing whatever appeared between the check and the delete;
            // this way the directory survives and the IOException says so.
            File.Delete(Path.Combine(path, MediaDirectory.MarkerName));
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Tagsmith: could not remove {Path}", path);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Whether a projection is going to want a media directory this run.
    /// </summary>
    /// <remarks>
    /// Seeding is a write, and it must not happen for a projection that
    /// <see cref="ProjectKindAsync"/> is about to decline to build — ticking "project award
    /// wins" without award tagging would otherwise leave an empty <c>tagsmith-award</c>
    /// directory behind on every run, which no teardown path reaches because there is
    /// nothing recorded to tear down. A projection Tagsmith already has a library for is
    /// always seeded: emptying out is a state a live projection passes through, and its
    /// directory going empty is what makes Jellyfin discard the library.
    /// </remarks>
    private static bool WillBuild(ProjectionKind kind, PluginConfiguration configuration) =>
        TagGrouping.IsEnabled(kind, configuration)
        && (TagGrouping.SourceIsTagged(kind, configuration)
            || configuration.ManagedLibraries.Any(l => l.Kind == kind));

    private string MediaPathFor(ProjectionKind kind) =>
        Path.Combine(_paths.DataPath, "tagsmith-" + kind.ToString().ToLowerInvariant());

    /// <summary>
    /// Creates a projection's media directory and makes sure Jellyfin will not discard it as
    /// empty. See <see cref="MediaDirectory"/> for why that matters and what it costs.
    /// </summary>
    private void SeedMediaDirectory(string mediaPath)
    {
        if (MediaDirectory.Seed(mediaPath))
        {
            _logger.LogDebug("Tagsmith: seeded {Path} so Jellyfin will not skip it as empty", mediaPath);
        }
    }

    private List<LibraryView> Survey() =>
        _libraryManager.GetVirtualFolders()
            .Select(f => new LibraryView(f.Name, f.ItemId, f.Locations ?? []))
            .ToList();

    private void Persist(Run run)
    {
        // Artwork changes track their dirtiness on the pass; everything else on the run.
        // One save covers both — it is the same configuration object either way.
        if ((!run.Dirty && !run.Pass.Dirty) || run.DryRun)
        {
            return;
        }

        // Serialised on the artwork gate: the adoption listener saves the same object from
        // Jellyfin's event thread, and two threads serialising into the same file is a
        // corrupt configuration.
        _artwork.PersistConfiguration();

        run.Dirty = false;
        run.Pass.Dirty = false;
    }

    private static void Forget(ProjectionKind kind, Run run)
    {
        var configuration = run.Configuration;
        configuration.ManagedCollections = configuration.ManagedCollections.Where(c => c.Kind != kind).ToArray();
        configuration.ManagedLibraries = configuration.ManagedLibraries.Where(l => l.Kind != kind).ToArray();
        run.Dirty = true;
    }

    private sealed class Run
    {
        public required PluginConfiguration Configuration { get; init; }

        public required bool DryRun { get; init; }

        /// <summary>
        /// Gets the artwork state this run carries: the resolved thumbnails folder and the
        /// artwork-side dirty flag. Handed to <see cref="ArtworkSynchronizer"/> on every
        /// artwork decision.
        /// </summary>
        public required ArtworkPass Pass { get; init; }

        /// <summary>
        /// Gets which of the three triggers this run is, which is the only thing that decides
        /// what happens to artwork. See <see cref="ArtworkPolicy"/>.
        /// </summary>
        public required ArtworkMode Artwork { get; init; }

        public bool Dirty { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this run has already revalidated the
        /// library roots. See <see cref="ResolveMediaFolderAsync"/>: it is a write, and one
        /// pass covers every projection.
        /// </summary>
        public bool RevalidatedLibraryRoots { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this run has already queued a full
        /// library scan. One scan resolves every projection's folders.
        /// </summary>
        public bool QueuedLibraryScan { get; set; }
    }

    /// <param name="ItemId">The CollectionFolder id.</param>
    /// <param name="Name">The library name as Jellyfin reports it.</param>
    /// <param name="MediaPath">The projection's private media directory.</param>
    /// <param name="Created">
    /// Whether this run created the library, which is what entitles the run to put the
    /// default tile artwork on it. See <see cref="ArtworkPolicy"/>.
    /// </param>
    private readonly record struct LibraryHandle(string ItemId, string Name, string MediaPath, bool Created = false);

    private readonly record struct PendingCollection(ManagedCollection Record, string Path);
}
