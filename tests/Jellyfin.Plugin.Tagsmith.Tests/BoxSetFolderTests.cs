using System.Xml.Linq;
using Jellyfin.Plugin.Tagsmith.Collections;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

/// <summary>
/// The on-disk contract with Jellyfin 10.11.11. Every assertion here mirrors a specific
/// piece of server behaviour, named in the test.
/// </summary>
public class BoxSetFolderTests
{
    private static readonly CollectionMember _film =
        new(Guid.Parse("11111111111111111111111111111111"), "/media/films/Pather Panchali.mkv");

    private static readonly CollectionMember _pathless =
        new(Guid.Parse("22222222222222222222222222222222"), null);

    // ------------------------------------------------------------ name sanitisation

    [Theory]
    [InlineData("Origins", "Origins")]
    [InlineData("  Origins  ", "Origins")]
    [InlineData("Origins/Countries", "Origins Countries")]
    [InlineData("Origins\\Countries", "Origins Countries")]
    [InlineData("A:B", "A B")]
    [InlineData("What?", "What ")]
    [InlineData("a*b|c<d>e\"f", "a b c d e f")]
    public void Sanitise_matches_GetValidFilename(string input, string expected) =>
        Assert.Equal(expected, BoxSetFolder.SanitiseName(input));

    [Fact]
    public void Sanitise_replaces_control_characters() =>
        Assert.Equal("a b c", BoxSetFolder.SanitiseName("a\u0001b\u001Fc"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitise_yields_nothing_for_a_blank_name(string? input) =>
        Assert.Equal(string.Empty, BoxSetFolder.SanitiseName(input));

    [Fact]
    public void A_sanitised_name_is_a_fixed_point()
    {
        // AddVirtualFolder sanitises what it is given, so comparing a configured name that
        // is not already sanitised against GetVirtualFolders() never matches — which is how
        // "Origins/Countries" produced Origins Countries, Origins Countries2, Origins
        // Countries3, one new library per run.
        var once = BoxSetFolder.SanitiseName("Origins/Countries");
        Assert.Equal(once, BoxSetFolder.SanitiseName(once));
    }

    // ------------------------------------------------------------ folder naming

    [Fact]
    public void Folder_name_carries_the_marker_BoxSetResolver_looks_for()
    {
        var name = BoxSetFolder.FolderNameFor("India");

        Assert.Equal("India [boxset]", name);
        Assert.Contains("[boxset]", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folder_name_strips_back_to_the_display_name()
    {
        // BoxSetResolver: Name = Path.GetFileName(args.Path).Replace("[boxset]", "").Trim()
        var name = BoxSetFolder.FolderNameFor("United States")!;
        Assert.Equal("United States", name.Replace("[boxset]", string.Empty, StringComparison.OrdinalIgnoreCase).Trim());
    }

    [Fact]
    public void Folder_name_sanitises_the_display_name() =>
        Assert.Equal("Bosnia Herzegovina [boxset]", BoxSetFolder.FolderNameFor("Bosnia/Herzegovina"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("///")]
    public void Folder_name_refuses_anything_that_is_not_a_directory(string value) =>
        Assert.Null(BoxSetFolder.FolderNameFor(value));

    // ------------------------------------------------------------ collection.xml

    private static XDocument Parse(string xml) => XDocument.Parse(xml);

    [Fact]
    public void Metadata_root_is_the_element_BaseXmlSaver_writes()
    {
        // BoxSetXmlSaver does not override GetRootElementName, so the root is "Item".
        var document = Parse(BoxSetFolder.BuildMetadata("India", [_film], locked: true));
        Assert.Equal("Item", document.Root!.Name.LocalName);
    }

    [Fact]
    public void Metadata_declares_utf8() =>
        Assert.Contains("encoding=\"utf-8\"", BoxSetFolder.BuildMetadata("India", [_film], true), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Members_go_under_CollectionItems_CollectionItem()
    {
        // BoxSetXmlParser.FetchFromCollectionItemsNode reads exactly these two element names.
        var document = Parse(BoxSetFolder.BuildMetadata("India", [_film, _pathless], true));
        var items = document.Root!.Element("CollectionItems")!.Elements("CollectionItem").ToArray();

        Assert.Equal(2, items.Length);
    }

    [Fact]
    public void A_member_with_a_path_is_referenced_by_path()
    {
        // BoxSetMetadataService.MergeData merges linked children with DistinctBy(i => i.Path),
        // so members that all carry a null path collapse into one. LinkedChild.Create makes
        // the same choice: Path first, LibraryItemId only when there is no path.
        var document = Parse(BoxSetFolder.BuildMetadata("India", [_film], true));
        var item = document.Root!.Element("CollectionItems")!.Element("CollectionItem")!;

        Assert.Equal("/media/films/Pather Panchali.mkv", item.Element("Path")!.Value);
        Assert.Null(item.Element("ItemId"));
    }

    [Fact]
    public void A_member_without_a_path_falls_back_to_its_id()
    {
        var document = Parse(BoxSetFolder.BuildMetadata("India", [_pathless], true));
        var item = document.Root!.Element("CollectionItems")!.Element("CollectionItem")!;

        // BaseItemXmlParser.GetLinkedChild reads ItemId into LibraryItemId, which
        // LibraryManagerExtensions.GetItemById(string) parses with new Guid(id) — the "N"
        // form LinkedChild.Create writes.
        Assert.Equal("22222222222222222222222222222222", item.Element("ItemId")!.Value);
        Assert.Null(item.Element("Path"));
    }

    [Fact]
    public void Every_member_is_written()
    {
        var members = Enumerable.Range(0, 50)
            .Select(i => new CollectionMember(Guid.NewGuid(), $"/media/{i}.mkv"))
            .ToArray();

        var document = Parse(BoxSetFolder.BuildMetadata("India", members, true));

        Assert.Equal(50, document.Root!.Element("CollectionItems")!.Elements("CollectionItem").Count());
    }

    [Fact]
    public void An_empty_collection_writes_no_CollectionItems_element()
    {
        // AddLinkedChildren returns early when there is nothing to write.
        var document = Parse(BoxSetFolder.BuildMetadata("India", [], true));
        Assert.Null(document.Root!.Element("CollectionItems"));
    }

    [Fact]
    public void The_display_name_is_written_as_LocalTitle()
    {
        var document = Parse(BoxSetFolder.BuildMetadata("United States", [_film], true));
        Assert.Equal("United States", document.Root!.Element("LocalTitle")!.Value);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void LockData_is_lowercased_like_the_saver_writes_it(bool locked, string expected)
    {
        // BaseItemXmlParser: IsLocked = string.Equals(value, "true", OrdinalIgnoreCase).
        var document = Parse(BoxSetFolder.BuildMetadata("India", [_film], locked));
        Assert.Equal(expected, document.Root!.Element("LockData")!.Value);
    }

    [Fact]
    public void Names_needing_escaping_survive_a_round_trip()
    {
        var document = Parse(BoxSetFolder.BuildMetadata("Ampersand & <Co>", [_film], true));
        Assert.Equal("Ampersand & <Co>", document.Root!.Element("LocalTitle")!.Value);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        // No timestamp, so an unchanged collection re-serialises byte for byte and the
        // caller can skip the write.
        var first = BoxSetFolder.BuildMetadata("India", [_film, _pathless], true);
        var second = BoxSetFolder.BuildMetadata("India", [_film, _pathless], true);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_changed_membership_changes_the_output()
    {
        var before = BoxSetFolder.BuildMetadata("India", [_film], true);
        var after = BoxSetFolder.BuildMetadata("India", [_film, _pathless], true);

        Assert.NotEqual(before, after);
    }
}
