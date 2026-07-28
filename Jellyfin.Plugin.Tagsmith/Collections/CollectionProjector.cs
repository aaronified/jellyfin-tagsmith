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
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<CollectionProjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionProjector"/> class.
    /// </summary>
    public CollectionProjector(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths paths,
        ILogger<CollectionProjector> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
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
            Thumbnails = new ThumbnailLocator(_paths.ProgramDataPath)
        };

        if (run.DryRun)
        {
            _logger.LogInformation("Tagsmith: [dry-run] projecting collections; nothing will be written");
        }

        // Every cancellation point used to skip the save at the end. Most of the state
        // self-heals on the next run, but one decision does not: a library the user deleted
        // in Jellyfin's own settings flips its projection off in memory, and losing that
        // means the next run recreates the library they deliberately removed.
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
    /// The scheduled run deliberately treats a poster it did not apply as the user's choice
    /// and adopts it. This is the escape hatch for when that is not what you wanted — after
    /// dropping in a new set of images, or after an adoption you would rather undo. It reads
    /// the folder only; collections with no matching file keep what they have.
    /// </remarks>
    public Task ReapplyArtworkAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return Task.CompletedTask;
        }

        var run = new Run
        {
            Configuration = configuration,
            DryRun = configuration.DryRun,
            Thumbnails = new ThumbnailLocator(_paths.ProgramDataPath),
            ForceArtwork = true
        };

        var records = configuration.ManagedCollections;
        if (records.Length == 0)
        {
            _logger.LogInformation("Tagsmith: no projected collections to apply artwork to");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Tagsmith: reapplying artwork from {Folder} to {Count} collections",
            run.Thumbnails.Root,
            records.Length);

        try
        {
            for (var i = 0; i < records.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = records[i];
                var boxSet = Resolve(record);

                if (boxSet is not null)
                {
                    SyncArtwork(record, boxSet, TagGrouping.NamespaceFor(record.Kind, configuration), run);
                }

                progress?.Report(100.0 * (i + 1) / records.Length);
            }
        }
        finally
        {
            Persist(run);
        }

        return Task.CompletedTask;
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

        var wanted = GroupItems(kind, items, tagNamespace, configuration.Separator);

        var library = await EnsureLibraryAsync(kind, run, cancellationToken).ConfigureAwait(false);
        if (library is null)
        {
            return;
        }

        PruneDuplicateRecords(kind, run);

        var pending = new List<PendingCollection>();

        foreach (var (value, members) in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = FindRecord(kind, value, run);
            var boxSet = Resolve(record);

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
                await SyncMembersAsync(boxSet, members, run, cancellationToken).ConfigureAwait(false);
                SyncArtwork(record!, boxSet, tagNamespace, run);
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
            await ResolveNewCollectionsAsync(library.Value, pending, tagNamespace, run, cancellationToken)
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
        var displayName = TagGrouping.DisplayName(value);

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
        string tagNamespace,
        Run run,
        CancellationToken cancellationToken)
    {
        if (_libraryManager.FindByPath(library.MediaPath, true) is not Folder mediaFolder)
        {
            // The library exists but its folder is not in the database yet. A full scan will
            // pick the collections up; their membership is already in the files we wrote.
            _logger.LogInformation(
                "Tagsmith: {Path} is not resolved yet; queueing a library scan to pick up {Count} new collections",
                library.MediaPath,
                pending.Count);
            _libraryManager.QueueLibraryScan();
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

            if (_libraryManager.FindByPath(entry.Path, true) is not BoxSet boxSet)
            {
                _logger.LogWarning(
                    "Tagsmith: wrote {Path} but Jellyfin has not resolved it as a collection yet; it will be picked up on the next run",
                    entry.Path);
                continue;
            }

            entry.Record.Id = boxSet.Id.ToString("N");
            run.Dirty = true;

            _logger.LogInformation("Tagsmith: created collection {Name} at {Path}", boxSet.Name, entry.Path);

            SyncArtwork(entry.Record, boxSet, tagNamespace, run);
        }
    }

    // ---------------------------------------------------------------- artwork

    /// <summary>
    /// Keeps the collection's poster and the thumbnails folder in step, in both
    /// directions.
    /// </summary>
    /// <remarks>
    /// A poster set by hand in the library UI is captured back into
    /// <c>&lt;config&gt;/tagsmith/thumbnails/</c> and becomes the stored artwork for that
    /// value, so it survives the collection being rebuilt and can be backed up or edited
    /// like any other file. Otherwise the file in that folder is applied to the
    /// collection.
    /// </remarks>
    private void SyncArtwork(ManagedCollection record, BaseItem boxSet, string tagNamespace, Run run)
    {
        // Forcing skips adoption deliberately: the whole point of the action is to discard
        // whatever poster is on the collection in favour of the file on disk.
        if (!run.ForceArtwork && CaptureManualPoster(record, boxSet, tagNamespace, run))
        {
            return;
        }

        var file = run.Thumbnails.Find(tagNamespace, record.Value);
        if (file is null)
        {
            return;
        }

        var hash = ThumbnailLocator.Hash(file);
        if (!run.ForceArtwork && string.Equals(record.ImageHash, hash, StringComparison.Ordinal))
        {
            return;
        }

        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would apply artwork {File} to {Name}",
                Path.GetFileName(file),
                boxSet.Name);
            return;
        }

        try
        {
            using (var stream = File.OpenRead(file))
            {
                _providerManager
                    .SaveImage(boxSet, stream, ThumbnailLocator.MimeTypeOf(file), ImageType.Primary, null, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            record.ImageHash = hash;

            // The server re-encodes on save, so the bytes on the item are not the bytes in
            // the thumbnails folder. Record what actually landed, or the next run reads
            // Tagsmith's own poster as user intent and copies it back over the source file.
            record.AppliedImageHash = CurrentPosterHash(boxSet) ?? string.Empty;
            run.Dirty = true;

            _logger.LogInformation("Tagsmith: applied artwork {File} to {Name}", Path.GetFileName(file), boxSet.Name);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Tagsmith: could not apply artwork {File}", file);
        }
    }

    /// <summary>
    /// If the collection carries a poster Tagsmith did not put there, copy it into the
    /// thumbnails folder and adopt it. Returns true when a capture happened, or would have.
    /// </summary>
    /// <remarks>
    /// "Tagsmith did not put there" is decided against <see cref="ManagedCollection.AppliedImageHash"/>,
    /// not against the source file's hash. Collections are also created locked, which keeps
    /// Jellyfin's remote image providers off them entirely
    /// (<c>ProviderManager.CanRefreshImages</c> returns false for a locked item outside a
    /// forced full refresh) — otherwise a provider-supplied poster would look exactly like a
    /// hand-set one and would be written over the user's curated artwork file.
    /// </remarks>
    private bool CaptureManualPoster(
        ManagedCollection record,
        BaseItem boxSet,
        string tagNamespace,
        Run run)
    {
        var image = boxSet.GetImageInfo(ImageType.Primary, 0);
        if (image?.Path is null || !File.Exists(image.Path))
        {
            return false;
        }

        var currentHash = ThumbnailLocator.Hash(image.Path);

        if (string.Equals(record.AppliedImageHash, currentHash, StringComparison.Ordinal))
        {
            return false;
        }

        // Records written before 0.0.5 kept only one hash; treat a match as ours.
        if (string.IsNullOrEmpty(record.AppliedImageHash)
            && string.Equals(record.ImageHash, currentHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (run.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would adopt the poster on {Name} into the thumbnails folder",
                boxSet.Name);
            return true;
        }

        try
        {
            var destination = run.Thumbnails.Store(tagNamespace, record.Value, image.Path);
            if (destination is null)
            {
                _logger.LogWarning(
                    "Tagsmith: cannot store artwork for {Namespace}/{Value}; check the namespace setting",
                    tagNamespace,
                    record.Value);
                return false;
            }

            record.ImageHash = ThumbnailLocator.Hash(destination);
            record.AppliedImageHash = currentHash;
            run.Dirty = true;

            _logger.LogInformation(
                "Tagsmith: adopted the poster on {Name} into {File}",
                boxSet.Name,
                destination);

            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Tagsmith: could not capture the poster on {Name}", boxSet.Name);
            return false;
        }
    }

    private static string? CurrentPosterHash(BaseItem boxSet)
    {
        var image = boxSet.GetImageInfo(ImageType.Primary, 0);
        return image?.Path is not null && File.Exists(image.Path) ? ThumbnailLocator.Hash(image.Path) : null;
    }

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
            Directory.CreateDirectory(mediaPath);

            // Awaited, unlike 0.0.4. AddVirtualFolder registers the CollectionFolder in its
            // finally block, via ValidateTopLibraryFolders — fire and forget meant ItemId was
            // still null on the lookup two lines later, so the first run created the library
            // and zero collections. It also meant the ArgumentException for a bad path was
            // swallowed as an unobserved task exception.
            //
            // refreshLibrary: false because Tagsmith runs its own scoped scan below; a full
            // background scan here would race it.
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

        return new LibraryHandle(created.ItemId, created.Name ?? plan.Name, mediaPath);
    }

    private LibraryHandle? Adopt(ProjectionKind kind, LibraryPlan plan, string mediaPath, Run run)
    {
        var folder = plan.Folder!.Value;
        if (folder.ItemId is null)
        {
            return null;
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

        SetEnabled(kind, run.Configuration, false);
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
        var configuration = run.Configuration;
        if (!configuration.RemoveCollectionsWhenDisabled)
        {
            return;
        }

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

        SweepBoxSetFolders(kind);
        RemoveMediaDirectory(kind);
        Forget(kind, run);
        _logger.LogInformation("Tagsmith: tore down the {Kind} projection", kind);
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
            _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true }, true);
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
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                _logger.LogInformation("Tagsmith: leaving {Path} in place; it is not empty", path);
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Tagsmith: could not remove {Path}", path);
        }
    }

    // ---------------------------------------------------------------- plumbing

    private string MediaPathFor(ProjectionKind kind) =>
        Path.Combine(_paths.DataPath, "tagsmith-" + kind.ToString().ToLowerInvariant());

    private List<LibraryView> Survey() =>
        _libraryManager.GetVirtualFolders()
            .Select(f => new LibraryView(f.Name, f.ItemId, f.Locations ?? []))
            .ToList();

    private void Persist(Run run)
    {
        if (!run.Dirty || run.DryRun)
        {
            return;
        }

        Plugin.Instance?.SaveConfiguration();
        run.Dirty = false;
    }

    private static void Forget(ProjectionKind kind, Run run)
    {
        var configuration = run.Configuration;
        configuration.ManagedCollections = configuration.ManagedCollections.Where(c => c.Kind != kind).ToArray();
        configuration.ManagedLibraries = configuration.ManagedLibraries.Where(l => l.Kind != kind).ToArray();
        run.Dirty = true;
    }

    private static void SetEnabled(ProjectionKind kind, PluginConfiguration configuration, bool enabled)
    {
        switch (kind)
        {
            case ProjectionKind.Origin:
                configuration.ProjectOrigin = enabled;
                break;
            case ProjectionKind.Language:
                configuration.ProjectLanguage = enabled;
                break;
            case ProjectionKind.Year:
                configuration.ProjectYear = enabled;
                break;
        }
    }

    private sealed class Run
    {
        public required PluginConfiguration Configuration { get; init; }

        public required bool DryRun { get; init; }

        public required ThumbnailLocator Thumbnails { get; init; }

        /// <summary>
        /// Gets a value indicating whether artwork in the thumbnails folder is applied
        /// regardless of what is currently on the collection. Set only by the explicit
        /// "reapply artwork" action, never by a scheduled run.
        /// </summary>
        public bool ForceArtwork { get; init; }

        public bool Dirty { get; set; }
    }

    private readonly record struct LibraryHandle(string ItemId, string Name, string MediaPath);

    private readonly record struct PendingCollection(ManagedCollection Record, string Path);
}
