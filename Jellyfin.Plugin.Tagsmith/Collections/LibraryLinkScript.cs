using System.Text;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// The page script that sends a <c>boxsets</c> library back to the scoped list view, and the
/// code that splices it into <c>index.html</c>.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin 10.11 opened a library tile at <c>#/list?parentId=&lt;id&gt;</c>. Jellyfin 12's web
/// client routes every <c>boxsets</c> library to <c>#/boxsets?topParentId=&lt;id&gt;</c>
/// instead, and the server then discards that parent for any <c>IncludeItemTypes=BoxSet</c>
/// request, so the library shows every collection on the server in one pile. The hand-typed
/// <c>list?parentId=</c> URL is unaffected, so the script redirects the one to the other.
/// See <c>JELLYFIN-12-MIGRATION.md</c>.
/// </para>
/// <para>
/// It applies to every <c>boxsets</c> library, not only Tagsmith's: the stock Collections
/// library suffers the same pile, now with Tagsmith's projected collections mixed into it,
/// and needs to open only its own (a Lord of the Rings box set, say). It uses
/// <c>location.replace</c> so Back returns to where the user came from rather than bouncing
/// off the redirect.
/// </para>
/// </remarks>
public static class LibraryLinkScript
{
    private const string Marker = "<!-- tagsmith-library-links -->";

    /// <summary>
    /// The script. It carries no per-server data, so a cached <c>index.html</c> never goes
    /// stale when a library is added.
    /// </summary>
    public const string Script = """
        (function () {
            // Safe to be loaded twice (a hand-pasted copy beside the plugin's own): the history
            // hooks must not be wrapped twice.
            if (window.__tagsmithLibraryLinks) { return; }
            window.__tagsmithLibraryLinks = true;
            function fix() {
                var m = /^#!?\/boxsets\?(.*)$/.exec(location.hash || '');
                if (!m) { return; }
                var id = (new URLSearchParams(m[1]).get('topParentId') || '').replace(/-/g, '').toLowerCase();
                if (!/^[0-9a-f]{32}$/.test(id)) { return; }
                var target = '#/list?parentId=' + id;
                var api = window.ApiClient;
                var server = api && api.serverId && api.serverId();
                if (server) { target += '&serverId=' + server; }
                location.replace(location.pathname + location.search + target);
            }
            // In-app navigation goes through the history API, which fires neither hashchange
            // nor popstate, so a tab click would slip past. Check after every push/replace;
            // the replace in fix() is a fragment navigation, which the router does hear.
            ['pushState', 'replaceState'].forEach(function (name) {
                var original = history[name];
                history[name] = function () {
                    var result = original.apply(this, arguments);
                    setTimeout(fix, 0);
                    return result;
                };
            });
            window.addEventListener('hashchange', fix);
            window.addEventListener('popstate', fix);
            fix();
        })();
        """;

    /// <summary>
    /// Inserts the script into an <c>index.html</c> document. Idempotent: a document that
    /// already carries the script is returned unchanged.
    /// </summary>
    public static string Inject(string html) => InjectBlock(html, Marker, Script);

    /// <summary>
    /// Splices a <c>&lt;script&gt;</c> block, led by an HTML comment that marks it as present,
    /// in before the closing body tag. Shared by every script Tagsmith adds to the page.
    /// </summary>
    internal static string InjectBlock(string html, string marker, string script)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (html.Contains(marker, StringComparison.Ordinal))
        {
            return html;
        }

        var block = new StringBuilder()
            .Append(marker).Append("<script>").Append(script).Append("</script>")
            .ToString();

        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + block : html.Insert(at, block);
    }
}
