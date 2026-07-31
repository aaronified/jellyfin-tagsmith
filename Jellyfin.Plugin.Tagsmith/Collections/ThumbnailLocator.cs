using System.Security.Cryptography;
using Jellyfin.Plugin.Tagsmith.Tagging;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Finds user-supplied collection artwork on disk.
/// </summary>
/// <remarks>
/// No artwork ships with the plugin. Users drop files into
/// <c>&lt;config&gt;/tagsmith/thumbnails/&lt;namespace&gt;/</c>; a starter set is published
/// separately in the repository. Filenames are matched by slugifying the stem, so
/// <c>india.png</c>, <c>India.PNG</c>, <c>United States.png</c> and
/// <c>united-states.png</c> all resolve.
/// </remarks>
public class ThumbnailLocator
{
    private static readonly string[] _extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailLocator"/> class.
    /// </summary>
    /// <param name="configurationRoot">Jellyfin's config directory.</param>
    public ThumbnailLocator(string configurationRoot)
    {
        _root = Path.GetFullPath(Path.Combine(configurationRoot, "tagsmith", "thumbnails"));
    }

    /// <summary>
    /// Picks the thumbnails root from the directories Jellyfin actually uses.
    /// </summary>
    /// <remarks>
    /// The documented location is <c>&lt;config&gt;/tagsmith/thumbnails</c>, where
    /// <c>&lt;config&gt;</c> is the Docker image's <c>/config</c> volume — which is
    /// <c>ProgramDataPath</c>, not <c>ConfigurationDirectoryPath</c>. On a native install the
    /// two diverge (<c>ConfigurationDirectoryPath</c> is <c>&lt;data&gt;/config</c>), and a
    /// user following the docs there lands in the wrong directory. So: prefer the primary
    /// location, but accept the alternate when the user has populated only that one.
    /// </remarks>
    /// <param name="programDataPath">The server's data directory (Docker's <c>/config</c>).</param>
    /// <param name="configurationDirectoryPath">The server's configuration directory.</param>
    public static ThumbnailLocator Resolve(string programDataPath, string? configurationDirectoryPath)
    {
        var primary = new ThumbnailLocator(programDataPath);
        if (Directory.Exists(primary.Root) || string.IsNullOrEmpty(configurationDirectoryPath))
        {
            return primary;
        }

        var alternate = new ThumbnailLocator(configurationDirectoryPath);
        return Directory.Exists(alternate.Root) ? alternate : primary;
    }

    /// <summary>
    /// Gets the directory users drop artwork into, for display in the settings page.
    /// </summary>
    public string Root => _root;

    /// <summary>
    /// Resolves the artwork directory for a namespace, or null when the namespace would
    /// escape the thumbnails tree.
    /// </summary>
    /// <remarks>
    /// The namespace is a free-text setting. A rooted value, or one containing <c>..</c> or a
    /// separator, would make <see cref="Store"/> run its "remove the old artwork for this
    /// value" delete loop somewhere else entirely. This is admin-only configuration rather
    /// than a privilege boundary, but a plugin has no business deleting files outside its own
    /// directory whatever the provenance of the setting.
    /// </remarks>
    public string? DirectoryFor(string? tagNamespace)
    {
        if (string.IsNullOrWhiteSpace(tagNamespace))
        {
            return null;
        }

        var trimmed = tagNamespace.Trim();
        if (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmed.Trim('.').Length == 0)
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Combine(_root, trimmed));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;

        return combined.StartsWith(prefix, StringComparison.Ordinal) ? combined : null;
    }

    /// <summary>
    /// Returns the artwork file for a value, or null when the user has supplied none.
    /// </summary>
    public string? Find(string tagNamespace, string value)
    {
        var directory = DirectoryFor(tagNamespace);
        if (directory is null)
        {
            return null;
        }

        return FindIn(directory, TagNormalizer.Slug(value));
    }

    /// <summary>
    /// Returns the artwork file for a projection's <em>library</em> — the Origins tile on
    /// the home screen rather than the India collection inside it — or null when the user
    /// has supplied none.
    /// </summary>
    /// <remarks>
    /// Library artwork sits at the root of the thumbnails tree, named after the namespace:
    /// <c>&lt;config&gt;/tagsmith/thumbnails/origin.png</c> beside the <c>origin/</c>
    /// directory that holds the per-value posters. Matching is by slug, same as values.
    /// </remarks>
    public string? FindLibrary(string tagNamespace)
    {
        // DirectoryFor is reused purely as the namespace validity check, so a namespace
        // that would escape the tree can never name a root-level file either.
        if (DirectoryFor(tagNamespace) is null)
        {
            return null;
        }

        return FindIn(_root, TagNormalizer.Slug(tagNamespace));
    }

    /// <summary>
    /// Finds the artwork file in one directory whose stem slugifies to the wanted slug.
    /// </summary>
    private static string? FindIn(string directory, string wanted)
    {
        // An empty slug would match every file whose stem also slugifies to nothing —
        // including the ".png" dotfile Store used to be able to write.
        if (wanted.Length == 0 || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!_extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(TagNormalizer.Slug(Path.GetFileNameWithoutExtension(file)), wanted, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies an image into the thumbnails folder as the stored artwork for a value,
    /// replacing whatever was there. Used to adopt a poster the user set by hand in the
    /// library UI, so it survives the collection being rebuilt.
    /// </summary>
    /// <returns>The path written, or null when the namespace or value is unusable.</returns>
    public string? Store(string tagNamespace, string value, string source)
    {
        var directory = DirectoryFor(tagNamespace);
        if (directory is null)
        {
            return null;
        }

        return StoreIn(directory, TagNormalizer.Slug(value), source);
    }

    /// <summary>
    /// Copies an image into the thumbnails folder as the stored artwork for a projection's
    /// library tile, replacing whatever was there. The library counterpart of
    /// <see cref="Store"/>; see <see cref="FindLibrary"/> for where the file lives.
    /// </summary>
    /// <returns>The path written, or null when the namespace is unusable.</returns>
    public string? StoreLibrary(string tagNamespace, string source)
    {
        if (DirectoryFor(tagNamespace) is null)
        {
            return null;
        }

        return StoreIn(_root, TagNormalizer.Slug(tagNamespace), source);
    }

    /// <summary>
    /// Writes artwork into one directory under a slug, deleting competing variants first.
    /// </summary>
    private static string? StoreIn(string directory, string slug, string source)
    {
        // A value like `origin=!!!` slugifies to nothing, which would write "<dir>/.png".
        if (slug.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(directory);

        // Replace any existing artwork for this slug, whatever extension it used, so one
        // value never ends up with two competing files.
        foreach (var stale in Directory.EnumerateFiles(directory))
        {
            if (_extensions.Contains(Path.GetExtension(stale), StringComparer.OrdinalIgnoreCase)
                && string.Equals(TagNormalizer.Slug(Path.GetFileNameWithoutExtension(stale)), slug, StringComparison.Ordinal))
            {
                File.Delete(stale);
            }
        }

        var extension = Path.GetExtension(source);
        if (!_extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            extension = ".png";
        }

        var destination = Path.Combine(directory, slug + extension.ToLowerInvariant());
        File.Copy(source, destination, true);
        return destination;
    }

    /// <summary>
    /// Hashes a file so Tagsmith can tell its own artwork from a poster the user picked by
    /// hand, and notice when the source file changes.
    /// </summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream));
    }

    /// <summary>
    /// Maps a file extension to the mime type Jellyfin wants when saving an image.
    /// </summary>
    public static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png"
    };
}
