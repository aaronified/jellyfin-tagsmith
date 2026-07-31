using Jellyfin.Plugin.Tagsmith.Collections;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class ThumbnailLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tagsmith-tests-" + Guid.NewGuid().ToString("N"));

    private ThumbnailLocator Locator => new(_root);

    private void Given(string tagNamespace, string fileName)
    {
        var directory = Path.Combine(_root, "tagsmith", "thumbnails", tagNamespace);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "not really a png");
    }

    [Theory]
    [InlineData("india.png")]
    [InlineData("India.PNG")]
    [InlineData("INDIA.png")]
    [InlineData("india.jpg")]
    [InlineData("india.webp")]
    public void Finds_artwork_regardless_of_case_or_extension(string fileName)
    {
        Given("origin", fileName);
        Assert.NotNull(Locator.Find("origin", "india"));
    }

    [Theory]
    [InlineData("United States.png")]
    [InlineData("united-states.png")]
    [InlineData("united_states.png")]
    public void Separators_in_filenames_do_not_matter(string fileName)
    {
        Given("origin", fileName);
        Assert.NotNull(Locator.Find("origin", "united_states"));
    }

    [Fact]
    public void Returns_null_when_the_user_supplied_nothing() =>
        Assert.Null(Locator.Find("origin", "india"));

    [Fact]
    public void Ignores_files_that_are_not_images()
    {
        Given("origin", "india.txt");
        Assert.Null(Locator.Find("origin", "india"));
    }

    [Fact]
    public void Namespaces_are_kept_apart()
    {
        Given("origin", "india.png");
        Assert.Null(Locator.Find("lang", "india"));
    }

    [Fact]
    public void Decade_artwork_resolves()
    {
        Given("year", "1950s.png");
        Assert.NotNull(Locator.Find("year", "1950s"));
    }

    [Fact]
    public void Hash_changes_when_the_file_does()
    {
        Given("origin", "india.png");
        var file = Locator.Find("origin", "india")!;
        var before = ThumbnailLocator.Hash(file);

        File.WriteAllText(file, "a different image");

        Assert.NotEqual(before, ThumbnailLocator.Hash(file));
    }

    [Fact]
    public void Store_adopts_a_manual_poster_into_the_folder()
    {
        var source = Path.Combine(_root, "manual.jpg");
        Directory.CreateDirectory(_root);
        File.WriteAllText(source, "hand-picked poster");

        var written = Locator.Store("origin", "india", source);

        Assert.NotNull(written);
        Assert.EndsWith(Path.Combine("origin", "india.jpg"), written, StringComparison.Ordinal);
        Assert.Equal("hand-picked poster", File.ReadAllText(written));
        Assert.NotNull(Locator.Find("origin", "india"));
    }

    [Fact]
    public void Store_replaces_existing_artwork_whatever_its_extension()
    {
        Given("origin", "India.PNG");
        var source = Path.Combine(_root, "manual.jpg");
        File.WriteAllText(source, "replacement");

        Locator.Store("origin", "india", source);

        var directory = Path.Combine(_root, "tagsmith", "thumbnails", "origin");
        Assert.Single(Directory.GetFiles(directory));
        Assert.Equal("replacement", File.ReadAllText(Locator.Find("origin", "india")!));
    }

    [Fact]
    public void Store_falls_back_to_png_for_an_unknown_extension()
    {
        var source = Path.Combine(_root, "poster.bin");
        Directory.CreateDirectory(_root);
        File.WriteAllText(source, "bytes");

        Assert.EndsWith(".png", Locator.Store("year", "1950s", source), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ library tiles

    [Theory]
    [InlineData("origin.png")]
    [InlineData("Origin.PNG")]
    [InlineData("origin.jpg")]
    public void A_library_tile_lives_at_the_root_named_after_the_namespace(string fileName)
    {
        var root = Path.Combine(_root, "tagsmith", "thumbnails");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, fileName), "library tile");

        Assert.NotNull(Locator.FindLibrary("origin"));
    }

    [Fact]
    public void A_library_tile_does_not_match_a_value_inside_the_namespace()
    {
        // thumbnails/origin/india.png is India's poster, not the Origins tile.
        Given("origin", "india.png");

        Assert.Null(Locator.FindLibrary("origin"));
    }

    [Fact]
    public void A_value_poster_does_not_match_the_library_tile()
    {
        var root = Path.Combine(_root, "tagsmith", "thumbnails");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "origin.png"), "library tile");

        Assert.Null(Locator.Find("origin", "origin"));
    }

    [Fact]
    public void StoreLibrary_adopts_a_manual_library_image()
    {
        var source = Path.Combine(_root, "manual.jpg");
        Directory.CreateDirectory(_root);
        File.WriteAllText(source, "hand-picked tile");

        var written = Locator.StoreLibrary("origin", source);

        Assert.NotNull(written);
        Assert.Equal(Path.Combine(Locator.Root, "origin.jpg"), written);
        Assert.Equal("hand-picked tile", File.ReadAllText(written));
        Assert.NotNull(Locator.FindLibrary("origin"));
    }

    [Fact]
    public void StoreLibrary_replaces_the_previous_tile_whatever_its_extension()
    {
        var root = Path.Combine(_root, "tagsmith", "thumbnails");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Origin.PNG"), "old tile");

        var source = Path.Combine(_root, "manual.jpg");
        File.WriteAllText(source, "new tile");
        Locator.StoreLibrary("origin", source);

        Assert.Single(Directory.GetFiles(root));
        Assert.Equal("new tile", File.ReadAllText(Locator.FindLibrary("origin")!));
    }

    [Theory]
    [InlineData("../../../etc")]
    [InlineData("origin/sub")]
    [InlineData("")]
    public void An_unusable_namespace_has_no_library_tile(string tagNamespace)
    {
        Assert.Null(Locator.FindLibrary(tagNamespace));
        Assert.Null(Locator.StoreLibrary(tagNamespace, Path.Combine(_root, "missing.png")));
    }

    // ------------------------------------------------------------ root resolution

    [Fact]
    public void Resolve_prefers_the_documented_root()
    {
        var primary = Path.Combine(_root, "data");
        var alternate = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(primary, "tagsmith", "thumbnails"));
        Directory.CreateDirectory(Path.Combine(alternate, "tagsmith", "thumbnails"));

        Assert.StartsWith(primary, ThumbnailLocator.Resolve(primary, alternate).Root, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_accepts_the_configuration_directory_when_only_it_is_populated()
    {
        // A native-install user following the "<config>" wording lands in
        // <data>/config/tagsmith/thumbnails; their images should still work.
        var primary = Path.Combine(_root, "data");
        var alternate = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(alternate, "tagsmith", "thumbnails"));

        Assert.StartsWith(alternate, ThumbnailLocator.Resolve(primary, alternate).Root, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_defaults_to_the_documented_root_when_neither_exists()
    {
        var primary = Path.Combine(_root, "data");
        var alternate = Path.Combine(_root, "config");

        Assert.StartsWith(primary, ThumbnailLocator.Resolve(primary, alternate).Root, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ containment

    [Theory]
    [InlineData("../../../etc")]
    [InlineData("..")]
    [InlineData("/etc")]
    [InlineData("origin/../../..")]
    [InlineData("origin/sub")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_namespace_can_never_escape_the_thumbnails_tree(string tagNamespace)
    {
        // Store runs a File.Delete loop over the directory it resolves, so a namespace that
        // escapes would have it deleting files somewhere else entirely.
        Assert.Null(Locator.DirectoryFor(tagNamespace));
        Assert.Null(Locator.Find(tagNamespace, "india"));
    }

    [Fact]
    public void An_escaping_namespace_stores_nothing()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "india.png");
        File.WriteAllText(victim, "someone else's file");

        var source = Path.Combine(_root, "manual.png");
        File.WriteAllText(source, "replacement");

        Assert.Null(Locator.Store("../../outside", "india", source));
        Assert.Equal("someone else's file", File.ReadAllText(victim));
    }

    [Fact]
    public void A_plain_namespace_resolves_inside_the_tree()
    {
        var directory = Locator.DirectoryFor("origin");

        Assert.NotNull(directory);
        Assert.StartsWith(Locator.Root, directory, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ empty slugs

    [Fact]
    public void A_value_that_slugifies_to_nothing_is_not_stored()
    {
        // `origin=!!!` slugifies to "", which used to write "<dir>/.png" — a dotfile that
        // then matched every other empty-stemmed file.
        var source = Path.Combine(_root, "manual.png");
        Directory.CreateDirectory(_root);
        File.WriteAllText(source, "poster");

        Assert.Null(Locator.Store("origin", "!!!", source));

        var directory = Path.Combine(_root, "tagsmith", "thumbnails", "origin");
        Assert.False(Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0);
    }

    [Fact]
    public void A_value_that_slugifies_to_nothing_matches_nothing()
    {
        Given("origin", ".png");
        Assert.Null(Locator.Find("origin", "!!!"));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.gif", "image/gif")]
    public void Mime_types_map_by_extension(string file, string expected) =>
        Assert.Equal(expected, ThumbnailLocator.MimeTypeOf(file));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        GC.SuppressFinalize(this);
    }
}
