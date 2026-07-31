using System.Globalization;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// A single member of a projected collection.
/// </summary>
/// <param name="Id">The library item id.</param>
/// <param name="Path">The item's path on disk, when it has one.</param>
public readonly record struct CollectionMember(Guid Id, string? Path);

/// <summary>
/// The on-disk contract for a box set: what the folder must be called, and what
/// <c>collection.xml</c> must contain, for Jellyfin's own resolver and parser to pick it up.
/// </summary>
/// <remarks>
/// <para>
/// Tagsmith writes the folder itself rather than calling
/// <c>ICollectionManager.CreateCollectionAsync</c>, because in 10.11.11 that method ignores
/// <c>CollectionCreationOptions.ParentId</c> entirely — it always resolves its parent with
/// <c>GetCollectionsFolder(true)</c>, which is hard-wired to
/// <c>Path.Combine(appPaths.DataPath, "collections")</c>. A box set belongs to whichever
/// library its folder sits in, so the only way to put one in a Tagsmith library is to create
/// the directory there.
/// </para>
/// <para>
/// Everything in this class is a transcription of Jellyfin 10.11.11 behaviour. Changing it
/// without re-reading the server source risks producing a directory that resolves as a plain
/// media folder, which is worse than producing nothing at all.
/// </para>
/// </remarks>
public static class BoxSetFolder
{
    /// <summary>
    /// The suffix <c>BoxSetResolver</c> looks for. It resolves a directory as a box set when
    /// the name contains <c>[boxset]</c> <em>or</em> the directory holds a
    /// <c>collection.xml</c>; we satisfy both, because the suffix alone is enough even if the
    /// metadata file is unreadable. The resolver then strips the suffix to derive the name.
    /// </summary>
    public const string Suffix = " [boxset]";

    /// <summary>
    /// The metadata file name, from <c>BoxSetXmlSaver.GetLocalSavePath</c> and
    /// <c>BoxSetXmlProvider.GetXmlFile</c>.
    /// </summary>
    public const string MetadataFileName = "collection.xml";

    /// <summary>
    /// The characters <c>ManagedFileSystem.GetValidFilename</c> replaces with a space in
    /// 10.11.11. Transcribed rather than taken from <c>IFileSystem</c> so the rule is
    /// testable and so folder naming and library-name validation cannot drift apart.
    /// </summary>
    private static readonly char[] _invalidFileNameCharacters =
    [
        '"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    ];

    /// <summary>
    /// Applies the same transformation <c>LibraryManager.AddVirtualFolder</c> applies to a
    /// library name before it creates the directory: trim, then replace every character
    /// Jellyfin considers invalid with a space.
    /// </summary>
    /// <remarks>
    /// Comparing a configured name against <c>GetVirtualFolders()</c> without this is the bug
    /// that made a name like <c>Origins/Countries</c> produce <c>Origins Countries</c>,
    /// <c>Origins Countries2</c>, <c>Origins Countries3</c>… one new library per run, because
    /// the configured name never matched what Jellyfin actually created.
    /// </remarks>
    public static string SanitiseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(_invalidFileNameCharacters) < 0)
        {
            return trimmed;
        }

        var buffer = trimmed.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (Array.IndexOf(_invalidFileNameCharacters, buffer[i]) >= 0)
            {
                buffer[i] = ' ';
            }
        }

        return new string(buffer);
    }

    /// <summary>
    /// Returns the directory name for a collection, or null when the display name sanitises
    /// to something that cannot be a directory.
    /// </summary>
    public static string? FolderNameFor(string displayName)
    {
        var sanitised = SanitiseName(displayName).Trim();

        // A name of "." or ".." would resolve to the library root or its parent.
        if (sanitised.Length == 0 || sanitised.Trim('.').Length == 0)
        {
            return null;
        }

        return sanitised + Suffix;
    }

    /// <summary>
    /// Builds the <c>collection.xml</c> Jellyfin's <c>BoxSetXmlParser</c> reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is taken from <c>BaseXmlSaver</c>: root element <c>Item</c> (BoxSetXmlSaver
    /// does not override <c>GetRootElementName</c>), members under
    /// <c>CollectionItems/CollectionItem</c>, each carrying <c>Path</c> and/or <c>ItemId</c>.
    /// </para>
    /// <para>
    /// Members are referenced by <b>path</b> wherever they have one. This is not a style
    /// choice: <c>BoxSetMetadataService.MergeData</c> merges linked children with
    /// <c>.DistinctBy(i =&gt; i.Path)</c>, so a file whose members all carry a null path
    /// collapses to a single member. <c>LinkedChild.Create</c> makes the same choice — it only
    /// falls back to <c>LibraryItemId</c> when the item has no path.
    /// </para>
    /// <para>
    /// No <c>Added</c> element is written even though the saver writes one, so the output is a
    /// pure function of the collection's content and an unchanged collection re-serialises
    /// byte for byte. That is what lets the caller skip the write when nothing changed.
    /// </para>
    /// </remarks>
    /// <param name="displayName">The collection's name.</param>
    /// <param name="members">The members, in order.</param>
    /// <param name="locked">
    /// Whether to lock the item. Tagsmith locks its collections: a locked item is skipped by
    /// every remote <em>image</em> provider
    /// (<c>ProviderManager.CanRefreshImages</c>: <c>if (item.IsLocked &amp;&amp;
    /// refreshOptions.ImageRefreshMode != MetadataRefreshMode.FullRefresh) return false;</c>),
    /// which stops a provider-supplied poster being mistaken for one the user set by hand,
    /// and by the remote metadata providers, which is what you want for an item called
    /// "India".
    /// <para>
    /// It does <b>not</b> keep the server's own <c>CollectionImageProvider</c> off — it turns
    /// it <em>on</em>. That provider's <c>Supports</c> begins <c>if (!item.IsLocked) return
    /// false;</c>, and it reaches the item at all because <c>BaseDynamicImageProvider</c> is
    /// an <c>ICustomMetadataProvider</c> and <c>IForcedProvider</c> rather than an
    /// <c>IImageProvider</c>, so neither gate above applies to it. It copies the first
    /// member's poster onto the box set on the single refresh where this element first flips
    /// <c>IsLocked</c>. <see cref="ArtworkSynchronizer"/> is what tells that poster apart
    /// from one a human uploaded; do not remove the lock to avoid it, or remote providers
    /// come back.
    /// </para>
    /// </param>
    public static string BuildMetadata(string displayName, IEnumerable<CollectionMember> members, bool locked)
    {
        ArgumentNullException.ThrowIfNull(members);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false)
        };

        using var text = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(text, settings))
        {
            writer.WriteStartDocument(true);
            writer.WriteStartElement("Item");

            writer.WriteElementString("LockData", locked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());

            if (!string.IsNullOrEmpty(displayName))
            {
                writer.WriteElementString("LocalTitle", displayName);
            }

            var written = 0;
            foreach (var member in members)
            {
                if (written == 0)
                {
                    writer.WriteStartElement("CollectionItems");
                }

                writer.WriteStartElement("CollectionItem");

                if (!string.IsNullOrWhiteSpace(member.Path))
                {
                    writer.WriteElementString("Path", member.Path);
                }
                else
                {
                    writer.WriteElementString("ItemId", member.Id.ToString("N", CultureInfo.InvariantCulture));
                }

                writer.WriteEndElement();
                written++;
            }

            if (written > 0)
            {
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return text.ToString();
    }

    /// <summary>
    /// <see cref="StringWriter"/> reports UTF-16 by default, which would put
    /// <c>encoding="utf-16"</c> in the declaration of a file we then write as UTF-8.
    /// </summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
