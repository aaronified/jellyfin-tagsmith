using Jellyfin.Plugin.Tagsmith.Collections;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class LibraryLinkScriptTests
{
    private const string Page = "<html><body><div id=\"app\"></div></body></html>";

    [Fact]
    public void The_script_is_spliced_in_before_the_closing_body_tag()
    {
        var result = LibraryLinkScript.Inject(Page);

        Assert.Contains(LibraryLinkScript.Script, result);
        Assert.True(result.IndexOf("<script>", StringComparison.Ordinal) < result.IndexOf("</body>", StringComparison.Ordinal));
        Assert.EndsWith("</body></html>", result);
    }

    [Fact]
    public void A_page_without_a_body_tag_gets_the_script_appended() =>
        Assert.Contains(LibraryLinkScript.Script, LibraryLinkScript.Inject("<html></html>"));

    [Fact]
    public void Injecting_twice_changes_nothing_the_second_time()
    {
        var once = LibraryLinkScript.Inject(Page);

        Assert.Equal(once, LibraryLinkScript.Inject(once));
    }

    [Fact]
    public void The_script_redirects_boxsets_to_the_scoped_list_route_for_any_library()
    {
        // The stock Collections library needs this as much as Tagsmith's own, so the script
        // must not carry an allow-list of ids.
        Assert.Contains("boxsets\\?", LibraryLinkScript.Script);
        Assert.Contains("topParentId", LibraryLinkScript.Script);
        Assert.Contains("#/list?parentId=", LibraryLinkScript.Script);
        Assert.Contains("location.replace", LibraryLinkScript.Script);
    }

    [Fact]
    public void The_registration_callback_leaves_the_page_alone_when_the_plugin_is_not_loaded()
    {
        // Plugin.Instance is null under test, which is the same path as the fix being off.
        var payload = new LibraryLinkRegistrar.Payload { Contents = Page };

        Assert.Equal(Page, LibraryLinkRegistrar.Transform(payload));
    }

    [Fact]
    public void The_script_hooks_the_history_api_because_in_app_navigation_never_fires_hashchange()
    {
        Assert.Contains("pushState", LibraryLinkScript.Script);
        Assert.Contains("replaceState", LibraryLinkScript.Script);
        Assert.Contains("popstate", LibraryLinkScript.Script);
    }

    [Fact]
    public void The_script_guards_against_running_twice() =>
        Assert.Contains("__tagsmithLibraryLinks", LibraryLinkScript.Script);
}
