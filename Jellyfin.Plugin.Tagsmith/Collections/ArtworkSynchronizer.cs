using Jellyfin.Plugin.Tagsmith.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// State one artwork pass carries between decisions: which folder is being read, whether
/// anything may be written, and whether configuration must be saved at the end.
/// </summary>
public sealed class ArtworkPass
{
    /// <summary>Gets the resolved thumbnails folder.</summary>
    public required ThumbnailLocator Thumbnails { get; init; }

    /// <summary>Gets a value indicating whether this pass logs instead of writing.</summary>
    public required bool DryRun { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether a record changed and configuration needs
    /// saving. Set by the synchroniser, cleared by <see cref="ArtworkSynchronizer.Persist"/>.
    /// </summary>
    public bool Dirty { get; set; }
}

/// <summary>
/// One thing artwork can be synced onto — a projected collection, or the library tile that
/// holds a projection — bundling the item, its bookkeeping record and where its artwork
/// file lives in the thumbnails folder.
/// </summary>
public sealed class ArtworkTarget
{
    private ArtworkTarget(
        BaseItem item,
        IArtworkRecord record,
        Func<ThumbnailLocator, string?> find,
        Func<ThumbnailLocator, string, string?> store)
    {
        Item = item;
        Record = record;
        Find = find;
        Store = store;
    }

    /// <summary>Gets the library item carrying the poster.</summary>
    public BaseItem Item { get; }

    /// <summary>Gets the record holding the two artwork hashes.</summary>
    public IArtworkRecord Record { get; }

    /// <summary>Gets the lookup for this target's file in the thumbnails folder.</summary>
    public Func<ThumbnailLocator, string?> Find { get; }

    /// <summary>Gets the writer that adopts a poster into the thumbnails folder.</summary>
    public Func<ThumbnailLocator, string, string?> Store { get; }

    /// <summary>
    /// A projected collection: its artwork file is named after the tag value, inside the
    /// namespace directory — <c>thumbnails/origin/india.png</c>.
    /// </summary>
    public static ArtworkTarget Collection(BaseItem boxSet, ManagedCollection record, string tagNamespace) =>
        new(
            boxSet,
            record,
            thumbnails => thumbnails.Find(tagNamespace, record.Value),
            (thumbnails, source) => thumbnails.Store(tagNamespace, record.Value, source));

    /// <summary>
    /// A projection's library: its tile file is named after the namespace itself, at the
    /// root of the thumbnails tree — <c>thumbnails/origin.png</c>.
    /// </summary>
    public static ArtworkTarget Library(BaseItem collectionFolder, ManagedLibrary record, string tagNamespace) =>
        new(
            collectionFolder,
            record,
            thumbnails => thumbnails.FindLibrary(tagNamespace),
            (thumbnails, source) => thumbnails.StoreLibrary(tagNamespace, source));
}

/// <summary>
/// Moves artwork between the thumbnails folder and the items Tagsmith owns, in both
/// directions but never in the same operation. <see cref="ArtworkPolicy"/> decides what a
/// trigger may do; this class carries it out and keeps the two directions from feeding
/// each other.
/// </summary>
/// <remarks>
/// <para>
/// Everything that reads or writes artwork state — the two hashes on a record, the
/// "currently applying" marker, and the <c>SaveConfiguration</c> that follows — is
/// serialised on one gate. The projection runs on a scheduled task thread, the adoption
/// listener on Jellyfin's event-dispatch thread, and the two share one configuration
/// object.
/// </para>
/// <para>
/// The gate is never held across the image save itself. Holding it there would risk a hang
/// if a future server version raised <c>ItemUpdated</c> from inside <c>SaveImage</c> on
/// another thread, and it is not needed: the marker covers that window instead.
/// </para>
/// </remarks>
public class ArtworkSynchronizer
{
    private readonly IProviderManager _providerManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ArtworkSynchronizer> _logger;

    /// <summary>
    /// Serialises artwork state. See the class remarks.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Items Tagsmith is applying a poster to right now, so the <c>ItemUpdated</c> listener
    /// can tell Tagsmith's own image update from somebody else's. Read and written only
    /// under <see cref="_gate"/>. See <see cref="AdoptPoster"/>.
    /// </summary>
    /// <remarks>
    /// A reference count, not a set: the sync task and the reapply task are separate
    /// scheduled tasks and can both be applying to the same item at once. With a set, the
    /// first to finish would drop the marker while the second was still inside its save,
    /// and that save's inline <c>ItemUpdated</c> would read as user intent.
    /// </remarks>
    private readonly Dictionary<Guid, int> _applying = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkSynchronizer"/> class.
    /// </summary>
    public ArtworkSynchronizer(
        IProviderManager providerManager,
        IApplicationPaths paths,
        ILogger<ArtworkSynchronizer> logger)
    {
        _providerManager = providerManager;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the thumbnails folder for a new pass, and says where it looked so "my
    /// images do nothing" can be diagnosed from the log alone.
    /// </summary>
    public ThumbnailLocator ResolveThumbnails()
    {
        var thumbnails = ThumbnailLocator.Resolve(_paths.ProgramDataPath, _paths.ConfigurationDirectoryPath);

        if (Directory.Exists(thumbnails.Root))
        {
            _logger.LogInformation(
                "Tagsmith: artwork folder {Root} ({Count} files)",
                thumbnails.Root,
                Directory.EnumerateFiles(thumbnails.Root, "*", SearchOption.AllDirectories).Count());
        }
        else
        {
            _logger.LogInformation(
                "Tagsmith: no artwork folder at {Root}; collections keep their default posters",
                thumbnails.Root);
        }

        return thumbnails;
    }

    /// <summary>
    /// Does whatever this trigger is supposed to do about one target's artwork.
    /// </summary>
    /// <remarks>
    /// The three triggers do one thing each and do not overlap: the scheduled run applies
    /// the thumbnails folder wherever that cannot destroy user intent, the
    /// <c>ItemUpdated</c> listener adopts a poster somebody else set, and the reapply
    /// action forces the folder onto everything. <see cref="ArtworkPolicy"/> holds that
    /// table; this method gathers its inputs and carries the verdict out.
    /// </remarks>
    public void Sync(ArtworkMode mode, ArtworkTarget target, bool createdThisRun, ArtworkPass pass)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pass);

        try
        {
            var file = target.Find(pass.Thumbnails);

            // Adoption reads the poster, not the folder, so a missing file only ends the
            // apply-flavoured modes early. The policy would answer None anyway; this exists
            // for the log line, which is the first thing to check when images "do nothing".
            if (file is null && mode != ArtworkMode.AdoptOnly)
            {
                _logger.LogDebug(
                    "Tagsmith: no artwork file for {Name} under {Root}",
                    target.Item.Name,
                    pass.Thumbnails.Root);
                return;
            }

            // The facts are read under the gate so the decision is made against a settled
            // record: the adoption listener mutates the same two hashes from Jellyfin's
            // event thread. The gate is not held across the apply that may follow.
            ArtworkFacts facts;
            lock (_gate)
            {
                facts = GatherFacts(target, file, createdThisRun);
            }

            switch (ArtworkPolicy.Decide(mode, facts))
            {
                case ArtworkAction.Apply:
                    ApplyStored(target, file!, pass, force: false);
                    break;

                case ArtworkAction.Reapply:
                    ApplyStored(target, file!, pass, force: true);
                    break;

                case ArtworkAction.Adopt:
                    CaptureManualPoster(target, pass);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hashing races the files it reads: an adoption can replace the artwork file
            // between Find and Hash. One unreadable file must cost one item's artwork, not
            // the rest of the run.
            _logger.LogWarning(ex, "Tagsmith: could not sync artwork for {Name}", target.Item.Name);
        }
    }

    /// <summary>
    /// Hashes the poster currently on an item, or null when it has none on disk.
    /// </summary>
    private static string? CurrentPosterHash(BaseItem item)
    {
        var image = item.GetImageInfo(ImageType.Primary, 0);
        return image?.Path is not null && File.Exists(image.Path) ? ThumbnailLocator.Hash(image.Path) : null;
    }

    /// <summary>
    /// Answers the six questions <see cref="ArtworkPolicy.Decide"/> asks about one target.
    /// </summary>
    private static ArtworkFacts GatherFacts(ArtworkTarget target, string? file, bool createdThisRun)
    {
        var record = target.Record;

        var fileHash = file is null ? null : ThumbnailLocator.Hash(file);

        // HasPoster is "the item declares a primary image", not "its bytes were readable".
        // An image whose file is momentarily unreadable — a network mount, an AV lock —
        // must not read as poster-less, because poster-less is what entitles the scheduled
        // run to write. Unreadable resolves to HasPoster true, PosterIsOwn false: None,
        // the safe verdict.
        var image = target.Item.GetImageInfo(ImageType.Primary, 0);
        var posterHash = image?.Path is not null && File.Exists(image.Path)
            ? ThumbnailLocator.Hash(image.Path)
            : null;

        // A poster is "Tagsmith's own" when it hashes to what Tagsmith's last SaveImage
        // produced. Records written before 0.0.5 kept only the source-file hash, so a match
        // on that is accepted as ours too.
        var posterIsOwn = posterHash is not null
            && (string.Equals(record.AppliedImageHash, posterHash, StringComparison.Ordinal)
                || (string.IsNullOrEmpty(record.AppliedImageHash)
                    && string.Equals(record.ImageHash, posterHash, StringComparison.Ordinal)));

        return new ArtworkFacts(
            CreatedThisRun: createdThisRun,
            HasArtworkFile: file is not null,
            HasPoster: image is not null,
            PosterIsOwn: posterIsOwn,
            FileChanged: fileHash is not null && !string.Equals(record.ImageHash, fileHash, StringComparison.Ordinal),
            PosterIsGenerated: IsServerGenerated(target.Item, image));
    }

    /// <summary>
    /// Whether a poster was produced by one of the server's own dynamic image providers
    /// rather than uploaded by a human.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mirrors the predicate the server itself uses to decide whether it owns an image:
    /// <c>BaseDynamicImageProvider.FetchAsync</c> regenerates only when the existing primary
    /// image is a local file underneath <c>GetInternalMetadataPath()</c>, and bails
    /// otherwise. Deliberately a little stricter than the server's
    /// <c>IFileSystem.ContainsSubPath</c>, which is a <c>Contains</c> rather than a
    /// <c>StartsWith</c> and normalises neither side; and case-insensitive on every platform
    /// rather than only on Windows, which is safe here because a user upload never reaches
    /// this tree at all — <c>BoxSet.IsSaveLocalMetadataEnabled()</c> and
    /// <c>CollectionFolder.IsSaveLocalMetadataEnabled()</c> both return true unconditionally,
    /// so <c>ImageSaver</c> always writes an upload beside the item.
    /// </para>
    /// <para>
    /// It matters because Tagsmith's own <c>&lt;LockData&gt;true&lt;/LockData&gt;</c> is what
    /// switches <c>CollectionImageProvider</c> on: its <c>Supports</c> returns false for an
    /// unlocked item. The provider runs on exactly one refresh pass — the one where
    /// <c>collection.xml</c> first flips <c>IsLocked</c> — which is the <c>ValidateChildren</c>
    /// Tagsmith itself triggers to resolve the folder it just wrote. So every projected
    /// collection acquires a copy of its first member's poster moments before Tagsmith gets
    /// to apply the artwork folder, and without this test that copy reads as a poster the
    /// user set by hand and vetoes the folder for good.
    /// </para>
    /// <para>
    /// The two locations are unambiguous: a dynamic provider passes
    /// <c>saveLocallyWithMedia: false</c>, which forces the image into
    /// <c>&lt;data&gt;/metadata/library/…</c>, while a web-UI upload goes through
    /// <c>ImageController</c> with no such override and lands beside the item as
    /// <c>&lt;box set folder&gt;/poster.*</c>. Do not test on the file name — both are
    /// "poster", and the extension follows whatever the copied source used.
    /// </para>
    /// </remarks>
    private static bool IsServerGenerated(BaseItem item, ItemImageInfo? image)
    {
        if (image?.Path is null || !image.IsLocalFile)
        {
            return false;
        }

        string metadata;
        string poster;
        try
        {
            metadata = Path.GetFullPath(item.GetInternalMetadataPath());
            poster = Path.GetFullPath(image.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (metadata.Length == 0)
        {
            return false;
        }

        return poster.StartsWith(
            metadata.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies the file in the thumbnails folder onto the item and persists the image
    /// through the repository, exactly as the server's own image upload endpoint does.
    /// </summary>
    /// <param name="target">What the poster goes on.</param>
    /// <param name="file">The artwork file to apply.</param>
    /// <param name="pass">The running pass.</param>
    /// <param name="force">
    /// Apply even when the file is the one Tagsmith last applied. Set by the reapply
    /// action, whose whole point is to discard whatever poster is on the item, and by the
    /// scheduled run when the item has no poster at all — there the record's hash may
    /// still match the file, and skipping would leave the item permanently blank.
    /// </param>
    private void ApplyStored(ArtworkTarget target, string file, ArtworkPass pass, bool force)
    {
        var item = target.Item;
        var record = target.Record;

        var hash = ThumbnailLocator.Hash(file);
        if (!force && string.Equals(record.ImageHash, hash, StringComparison.Ordinal))
        {
            return;
        }

        if (pass.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would apply artwork {File} to {Name}",
                Path.GetFileName(file),
                item.Name);
            return;
        }

        // Claimed before anything is written and released in the finally below, both under
        // the gate the adoption listener decides under. The record is updated under the
        // same gate before the release, so the listener never sees a state in between —
        // see the loop guard on AdoptPoster. The gate is deliberately not held across the
        // save itself.
        lock (_gate)
        {
            _applying[item.Id] = _applying.GetValueOrDefault(item.Id) + 1;
        }

        try
        {
            using (var stream = File.OpenRead(file))
            {
                _providerManager
                    .SaveImage(item, stream, ThumbnailLocator.MimeTypeOf(file), ImageType.Primary, null, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            // SaveImage writes the file and calls SetImagePath, which mutates ImageInfos in
            // memory only — it never touches the repository. Without this the poster is
            // lost on restart. It is also what recomputes the blurhash and DateModified the
            // clients derive their image cache tags from, so skipping it leaves every
            // client showing the old tile. ImageController does the same thing after its
            // own SaveImage.
            item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            lock (_gate)
            {
                record.ImageHash = hash;

                // The server re-encodes on save, so the bytes on the item are not the bytes
                // in the thumbnails folder. Record what actually landed, or Tagsmith reads
                // its own poster as user intent and copies it back over the source file.
                record.AppliedImageHash = CurrentPosterHash(item) ?? string.Empty;
            }

            pass.Dirty = true;

            _logger.LogInformation("Tagsmith: applied artwork {File} to {Name}", Path.GetFileName(file), item.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Tagsmith: could not apply artwork {File} to {Name}", file, item.Name);
        }
        finally
        {
            // Exactly one decrement per apply, whatever happened above. A failed save must
            // not leave the item marked, or its poster could never be adopted again — and
            // a concurrent apply from the other scheduled task must keep its own claim.
            lock (_gate)
            {
                if (_applying.TryGetValue(item.Id, out var claims))
                {
                    if (claims <= 1)
                    {
                        _applying.Remove(item.Id);
                    }
                    else
                    {
                        _applying[item.Id] = claims - 1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// If the item carries a poster Tagsmith did not put there, copy it into the thumbnails
    /// folder and adopt it. Returns true when a capture happened, or would have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Tagsmith did not put there" is decided against <see cref="IArtworkRecord.AppliedImageHash"/>,
    /// not against the source file's hash. Collections are also created locked, which keeps
    /// Jellyfin's remote image providers off them entirely
    /// (<c>ProviderManager.CanRefreshImages</c> returns false for a locked item outside a
    /// forced full refresh) — otherwise a provider-supplied poster would look exactly like
    /// a hand-set one and would be written over the user's curated artwork file.
    /// </para>
    /// <para>
    /// The lock does not stop the server's <em>own</em> <c>CollectionImageProvider</c>, which
    /// is not an image provider at all and which the lock in fact enables. That is what
    /// <see cref="IsServerGenerated"/> is for, and it is why the policy refuses to adopt a
    /// generated poster: doing so would copy a member's poster over the user's curated file
    /// for that value.
    /// </para>
    /// <para>
    /// That comparison is the standing half of the loop guard, and it holds no matter how
    /// long after the fact an image update arrives. It writes into the thumbnails folder
    /// only, never to the item, so adoption raises no image update of its own and cannot
    /// re-trigger itself.
    /// </para>
    /// <para>
    /// Library tiles are not locked the way box sets are — <c>AddVirtualFolder</c> sets no
    /// <c>LockData</c> — so the "providers are kept off entirely" half of the argument does
    /// not carry over to them. In practice the only provider that can attach an image to a
    /// <c>boxsets</c> CollectionFolder is the local one, and what it finds is the file
    /// Tagsmith's own save wrote, which the hash comparison above recognises and rejects.
    /// </para>
    /// </remarks>
    private bool CaptureManualPoster(ArtworkTarget target, ArtworkPass pass)
    {
        var item = target.Item;
        var record = target.Record;

        var image = item.GetImageInfo(ImageType.Primary, 0);
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

        if (pass.DryRun)
        {
            _logger.LogInformation(
                "Tagsmith: [dry-run] would adopt the poster on {Name} into the thumbnails folder",
                item.Name);
            return true;
        }

        try
        {
            var destination = target.Store(pass.Thumbnails, image.Path);
            if (destination is null)
            {
                _logger.LogWarning(
                    "Tagsmith: cannot store artwork for {Name}; check the namespace setting",
                    item.Name);
                return false;
            }

            record.ImageHash = ThumbnailLocator.Hash(destination);
            record.AppliedImageHash = currentHash;
            pass.Dirty = true;

            _logger.LogInformation(
                "Tagsmith: adopted the poster on {Name} into {File}",
                item.Name,
                destination);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Tagsmith: could not capture the poster on {Name}", item.Name);
            return false;
        }
    }

    /// <summary>
    /// Adopts the poster now on an item Tagsmith owns into the thumbnails folder. Called by
    /// <see cref="PosterAdoptionListener"/> the moment somebody sets one — on a projected
    /// collection or on one of Tagsmith's library tiles.
    /// </summary>
    /// <param name="item">The item Jellyfin reported an image update for.</param>
    /// <remarks>
    /// <para>
    /// Runs on Jellyfin's own event-dispatch thread, so it does the least it can: type
    /// check, a lookup in the managed records, and for the one item in a library that
    /// matches, a hash and a file copy. Nothing is queued to a worker — see the loop guard
    /// below for why moving the work off this thread would break it.
    /// </para>
    /// <para>
    /// <b>Loop guard.</b> Applying artwork writes to the item, and writing to the item can
    /// produce an image update of its own, which would land back here and copy Tagsmith's
    /// own poster over the user's source file. Two things stop that.
    /// </para>
    /// <para>
    /// Adoption only ever writes into the thumbnails folder, never to the item, so it
    /// raises no image update and cannot re-trigger itself. At most one update exists per
    /// apply; the two directions cannot ping-pong.
    /// </para>
    /// <para>
    /// And that one update cannot be mistaken for user intent. <see cref="ApplyStored"/>
    /// adds the item id to <see cref="_applying"/> under <see cref="_gate"/> before it
    /// writes anything, and removes it again under the same gate in the same step that
    /// records <see cref="IArtworkRecord.AppliedImageHash"/>. Everything below reads and
    /// decides under that gate too, so it sees one of three states and never a state in
    /// between: before the apply, in which case the poster it adopts is genuinely the
    /// user's; during it, in which case the marker is present and it stands off; or after
    /// it, in which case the hash comparison in <see cref="CaptureManualPoster"/>
    /// recognises Tagsmith's own poster and rejects it. This is also why the work is done
    /// inline rather than queued: a worker would run after the gate had been released and
    /// the marker dropped.
    /// </para>
    /// </remarks>
    public void AdoptPoster(BaseItem? item)
    {
        // The event fires for every item in the library, so bail out on the cheapest test
        // first. Only a BoxSet can be one of Tagsmith's collections, and only a
        // CollectionFolder one of its library tiles.
        if (item is not BoxSet && item is not CollectionFolder)
        {
            return;
        }

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        // Ownership is by recorded id, exactly as everywhere else: a box set or library the
        // user made themselves is never touched, whatever it is called.
        var target = FindOwnedTarget(item, configuration);
        if (target is null)
        {
            return;
        }

        var pass = new ArtworkPass
        {
            Thumbnails = ThumbnailLocator.Resolve(_paths.ProgramDataPath, _paths.ConfigurationDirectoryPath),
            DryRun = configuration.DryRun
        };

        // Configuration is shared with whatever projection run may be going on, and both
        // sides read-modify-write the same record and then serialise the whole file. The
        // read of the poster, the decision and the write all have to sit inside one
        // critical section, or the marker check below can be answered from before an apply
        // that has started by the time the poster is read.
        lock (_gate)
        {
            if (_applying.ContainsKey(item.Id))
            {
                // Tagsmith's own SaveImage, still in flight. Adopting here would copy the
                // poster Tagsmith just applied back over the file it read it from.
                return;
            }

            Sync(ArtworkMode.AdoptOnly, target, createdThisRun: false, pass);
            Persist(pass);
        }
    }

    /// <summary>
    /// Resolves an updated item to the artwork target Tagsmith owns for it, or null when
    /// the item is not Tagsmith's.
    /// </summary>
    private static ArtworkTarget? FindOwnedTarget(BaseItem item, PluginConfiguration configuration)
    {
        if (item is BoxSet)
        {
            var record = Array.Find(
                configuration.ManagedCollections,
                c => Guid.TryParse(c.Id, out var id) && id.Equals(item.Id));

            if (record is null)
            {
                return null;
            }

            var tagNamespace = TagGrouping.NamespaceFor(record.Kind, configuration);
            return string.IsNullOrWhiteSpace(tagNamespace)
                ? null
                : ArtworkTarget.Collection(item, record, tagNamespace);
        }

        // CollectionFolder — one of the projection libraries, matched on the id recorded
        // when the library was created or adopted.
        var library = Array.Find(
            configuration.ManagedLibraries,
            l => Guid.TryParse(l.ItemId, out var id) && id.Equals(item.Id));

        if (library is null)
        {
            return null;
        }

        var libraryNamespace = TagGrouping.NamespaceFor(library.Kind, configuration);
        return string.IsNullOrWhiteSpace(libraryNamespace)
            ? null
            : ArtworkTarget.Library(item, library, libraryNamespace);
    }

    /// <summary>
    /// Saves configuration if a pass changed it. Serialised on the artwork gate — two
    /// threads serialising the same object into the same file is a corrupt configuration
    /// file. Monitor is reentrant, so <see cref="AdoptPoster"/> calling this under its own
    /// lock is fine.
    /// </summary>
    public void Persist(ArtworkPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        if (!pass.Dirty || pass.DryRun)
        {
            return;
        }

        PersistConfiguration();
        pass.Dirty = false;
    }

    /// <summary>
    /// Saves configuration under the artwork gate, for callers whose dirtiness is tracked
    /// elsewhere. The projector shares its configuration object with the adoption listener,
    /// so its saves must be serialised on the same lock as everything above.
    /// </summary>
    /// <remarks>
    /// Guards dry run itself rather than trusting every caller to: this is the one method
    /// on the class that writes to disk, in a codebase whose ground rule is that a dry run
    /// writes nothing.
    /// </remarks>
    public void PersistConfiguration()
    {
        if (Plugin.Instance?.Configuration.DryRun == true)
        {
            _logger.LogDebug("Tagsmith: [dry-run] configuration not saved");
            return;
        }

        lock (_gate)
        {
            Plugin.Instance?.SaveConfiguration();
        }
    }
}
