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
