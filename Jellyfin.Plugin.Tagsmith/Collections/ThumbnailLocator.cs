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
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        // An empty slug would match every file whose stem also slugifies to nothing —
        // including the ".png" dotfile Store used to be able to write.
        var wanted = TagNormalizer.Slug(value);
        if (wanted.Length == 0)
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

        // A value like `origin=!!!` slugifies to nothing, which would write "<dir>/.png".
        var slug = TagNormalizer.Slug(value);
        if (slug.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(directory);

        // Replace any existing artwork for this value, whatever extension it used, so one
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
