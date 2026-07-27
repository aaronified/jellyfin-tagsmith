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
        _root = Path.Combine(configurationRoot, "tagsmith", "thumbnails");
    }

    /// <summary>
    /// Gets the directory users drop artwork into, for display in the settings page.
    /// </summary>
    public string Root => _root;

    /// <summary>
    /// Returns the artwork file for a value, or null when the user has supplied none.
    /// </summary>
    public string? Find(string tagNamespace, string value)
    {
        var directory = Path.Combine(_root, tagNamespace);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var wanted = TagNormalizer.Slug(value);

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
