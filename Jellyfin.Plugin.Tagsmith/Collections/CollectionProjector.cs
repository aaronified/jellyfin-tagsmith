using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Tagsmith.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Projects the tag set into browsable collection libraries, for the many clients that
/// neither display nor filter on tags.
/// </summary>
/// <remarks>
/// Tags stay the source of truth; collections are rebuilt from them. Tagsmith records the
/// id of everything it creates and only ever modifies or deletes those, so a hand-made
/// collection sharing a name is never touched.
/// </remarks>
public class CollectionProjector
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<CollectionProjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionProjector"/> class.
    /// </summary>
    public CollectionProjector(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        IApplicationPaths paths,
        ILogger<CollectionProjector> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles every projection against the current tag set.
    /// </summary>
    public async Task ProjectAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var thumbnails = new ThumbnailLocator(_paths.ProgramDataPath);

        foreach (var kind in Enum.GetValues<ProjectionKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReconcileDeletedLibrary(kind, configuration))
            {
                continue;
            }

            if (!TagGrouping.IsEnabled(kind, configuration))
            {
                await TearDownAsync(kind, configuration, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ProjectKindAsync(kind, items, configuration, thumbnails, cancellationToken)
                .ConfigureAwait(false);
        }

        Plugin.Instance?.SaveConfiguration();
    }

    /// <summary>
    /// If the user deleted one of our libraries in Jellyfin's own settings, treat that as
    /// intent to disable rather than something to undo. The failure worth designing
    /// against is a nightly task silently resurrecting a library the user removed.
    /// </summary>
    private bool ReconcileDeletedLibrary(ProjectionKind kind, PluginConfiguration configuration)
    {
        var record = configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind);
        if (record is null || !TagGrouping.IsEnabled(kind, configuration))
        {
            return false;
        }

        if (_libraryManager.GetVirtualFolders().Any(f => string.Equals(f.Name, record.Name, StringComparison.Ordinal)))
        {
            return false;
        }

        _logger.LogInformation(
            "Tagsmith: library {Library} was removed outside the plugin; disabling the {Kind} projection rather than recreating it",
            record.Name,
            kind);

        SetEnabled(kind, configuration, false);
        Forget(kind, configuration);
        return true;
    }

    private async Task ProjectKindAsync(
        ProjectionKind kind,
        IReadOnlyList<BaseItem> items,
        PluginConfiguration configuration,
        ThumbnailLocator thumbnails,
        CancellationToken cancellationToken)
    {
        var tagNamespace = TagGrouping.NamespaceFor(kind, configuration);
        if (string.IsNullOrWhiteSpace(tagNamespace))
        {
            return;
        }

        var wanted = GroupItems(kind, items, tagNamespace, configuration.Separator);
        var libraryId = await EnsureLibraryAsync(kind, configuration).ConfigureAwait(false);
        if (libraryId is null)
        {
            return;
        }

        var owned = configuration.ManagedCollections.Where(c => c.Kind == kind).ToList();

        foreach (var (value, memberIds) in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = owned.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
            var boxSet = record is null ? null : _libraryManager.GetItemById(Guid.Parse(record.Id));

            if (boxSet is null)
            {
                var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = TagGrouping.DisplayName(value),
                    ParentId = libraryId,
                    ItemIdList = memberIds.Select(id => id.ToString("N")).ToArray(),
                    IsLocked = false
                }).ConfigureAwait(false);

                record = new ManagedCollection { Kind = kind, Value = value, Id = created.Id.ToString("N") };
                configuration.ManagedCollections = [.. configuration.ManagedCollections, record];
                boxSet = created;

                _logger.LogInformation("Tagsmith: created collection {Name} with {Count} items", created.Name, memberIds.Count);
            }
            else
            {
                await SyncMembersAsync(boxSet, memberIds, cancellationToken).ConfigureAwait(false);
            }

            ApplyArtwork(record!, boxSet, tagNamespace, thumbnails);
        }

        await RemoveEmptyAsync(kind, wanted.Keys, configuration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups items by projected value, reading the tags on the item so hand-added tags
    /// are honoured alongside generated ones.
    /// </summary>
    private static Dictionary<string, List<Guid>> GroupItems(
        ProjectionKind kind,
        IReadOnlyList<BaseItem> items,
        string tagNamespace,
        string separator)
    {
        var grouped = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);

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
                }

                if (!members.Contains(item.Id))
                {
                    members.Add(item.Id);
                }
            }
        }

        return grouped;
    }

    private async Task SyncMembersAsync(BaseItem boxSet, List<Guid> wanted, CancellationToken cancellationToken)
    {
        // Collection membership is held as linked children, not as real parenting.
        var current = boxSet is Folder folder
            ? folder.GetLinkedChildren().Select(c => c.Id).ToHashSet()
            : [];

        var toAdd = wanted.Where(id => !current.Contains(id)).ToArray();
        var toRemove = current.Where(id => !wanted.Contains(id)).ToArray();

        if (toAdd.Length > 0)
        {
            await _collectionManager.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);
        }

        if (toRemove.Length > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, toRemove).ConfigureAwait(false);
        }

        if (toAdd.Length > 0 || toRemove.Length > 0)
        {
            _logger.LogDebug(
                "Tagsmith: {Name} +{Added} -{Removed}",
                boxSet.Name,
                toAdd.Length,
                toRemove.Length);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Applies user artwork, but only when the collection has none or when the image
    /// currently on it is one Tagsmith applied and the source file has since changed.
    /// A poster the user picked by hand must survive the nightly run.
    /// </summary>
    private void ApplyArtwork(ManagedCollection record, BaseItem boxSet, string tagNamespace, ThumbnailLocator thumbnails)
    {
        var file = thumbnails.Find(tagNamespace, record.Value);
        if (file is null)
        {
            return;
        }

        var hash = ThumbnailLocator.Hash(file);
        if (string.Equals(record.ImageHash, hash, StringComparison.Ordinal))
        {
            return;
        }

        var hasImage = boxSet.HasImage(ImageType.Primary, 0);
        var oursIsCurrent = record.ImageHash.Length > 0;

        if (hasImage && !oursIsCurrent)
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(file);
            _providerManager
                .SaveImage(boxSet, stream, ThumbnailLocator.MimeTypeOf(file), ImageType.Primary, null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            record.ImageHash = hash;
            _logger.LogInformation("Tagsmith: applied artwork {File} to {Name}", Path.GetFileName(file), boxSet.Name);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Tagsmith: could not apply artwork {File}", file);
        }
    }

    /// <summary>
    /// Creates the collections library if it is missing, and follows a rename of the
    /// configured library name.
    /// </summary>
    private async Task<Guid?> EnsureLibraryAsync(ProjectionKind kind, PluginConfiguration configuration)
    {
        var wantedName = TagGrouping.LibraryNameFor(kind, configuration);
        if (string.IsNullOrWhiteSpace(wantedName))
        {
            return null;
        }

        var record = configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind);

        // Jellyfin 10.11 exposes no rename for virtual folders, so a renamed library is
        // torn down and rebuilt. The collections are regenerated from tags below, so
        // nothing is lost but the user's per-user access grants.
        if (record is not null && !string.Equals(record.Name, wantedName, StringComparison.Ordinal))
        {
            _logger.LogInformation("Tagsmith: library renamed {Old} -> {New}", record.Name, wantedName);
            await RemoveLibraryAsync(record.Name).ConfigureAwait(false);
            Forget(kind, configuration);
            record = null;
        }

        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, wantedName, StringComparison.Ordinal));

        if (existing is null)
        {
            var path = Path.Combine(_paths.DataPath, "tagsmith-" + kind.ToString().ToLowerInvariant());
            Directory.CreateDirectory(path);

            _libraryManager.AddVirtualFolder(
                wantedName,
                CollectionTypeOptions.boxsets,
                new LibraryOptions { PathInfos = [new MediaPathInfo(path)] },
                true);

            existing = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => string.Equals(f.Name, wantedName, StringComparison.Ordinal));

            _logger.LogInformation("Tagsmith: created library {Name} at {Path}", wantedName, path);
        }

        if (existing?.ItemId is null || !Guid.TryParse(existing.ItemId, out var id))
        {
            return null;
        }

        if (record is null)
        {
            configuration.ManagedLibraries =
                [.. configuration.ManagedLibraries, new ManagedLibrary { Kind = kind, Name = wantedName }];
        }

        return id;
    }

    private async Task RemoveEmptyAsync(
        ProjectionKind kind,
        IEnumerable<string> stillWanted,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var wanted = stillWanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = configuration.ManagedCollections
            .Where(c => c.Kind == kind && !wanted.Contains(c.Value))
            .ToArray();

        foreach (var record in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteCollectionAsync(record).ConfigureAwait(false);
        }

        if (stale.Length > 0)
        {
            configuration.ManagedCollections = configuration.ManagedCollections.Except(stale).ToArray();
        }
    }

    private async Task TearDownAsync(
        ProjectionKind kind,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.RemoveCollectionsWhenDisabled)
        {
            return;
        }

        var owned = configuration.ManagedCollections.Where(c => c.Kind == kind).ToArray();
        if (owned.Length == 0 && configuration.ManagedLibraries.All(l => l.Kind != kind))
        {
            return;
        }

        foreach (var record in owned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteCollectionAsync(record).ConfigureAwait(false);
        }

        var library = configuration.ManagedLibraries.FirstOrDefault(l => l.Kind == kind);
        if (library is not null)
        {
            await RemoveLibraryAsync(library.Name).ConfigureAwait(false);
        }

        Forget(kind, configuration);
        _logger.LogInformation("Tagsmith: tore down the {Kind} projection", kind);
    }

    private async Task DeleteCollectionAsync(ManagedCollection record)
    {
        if (!Guid.TryParse(record.Id, out var id))
        {
            return;
        }

        var boxSet = _libraryManager.GetItemById(id);
        if (boxSet is null)
        {
            return;
        }

        _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true }, true);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RemoveLibraryAsync(string name)
    {
        if (_libraryManager.GetVirtualFolders().Any(f => string.Equals(f.Name, name, StringComparison.Ordinal)))
        {
            await _libraryManager.RemoveVirtualFolder(name, true).ConfigureAwait(false);
        }
    }

    private static void Forget(ProjectionKind kind, PluginConfiguration configuration)
    {
        configuration.ManagedCollections = configuration.ManagedCollections.Where(c => c.Kind != kind).ToArray();
        configuration.ManagedLibraries = configuration.ManagedLibraries.Where(l => l.Kind != kind).ToArray();
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
}
