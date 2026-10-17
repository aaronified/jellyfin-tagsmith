namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// The page script that adds a Tagsmith entry to the avatar menu of the Jellyfin 12 web
/// client, next to Settings.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin 12 has no navigation drawer, so a plugin's configuration page is no longer
/// reachable from the sidebar, and Jellyfin's plugin model has no flag for the avatar menu
/// either. Jellyscribe and Jellyfin Enhanced both reach it the same way: clone the menu's own
/// Settings item, so the entry carries the menu's styling, and insert the clone after it.
/// The menu is a popover that is rendered into <c>&lt;body&gt;</c> on open, so the script
/// watches body's direct children.
/// </para>
/// <para>
/// The entry opens Tagsmith's settings page, which is an administrator's page, so it is only
/// added for administrators. It never touches a menu that does not carry a Settings item,
/// which keeps it inert on the 10.11 client.
/// </para>
/// </remarks>
public static class UserMenuScript
{
    private const string Marker = "<!-- tagsmith-user-menu -->";

    /// <summary>
    /// The script. It carries no per-server data.
    /// </summary>
    public const string Script = """
        (function () {
            if (window.__tagsmithUserMenu) { return; }
            window.__tagsmithUserMenu = true;
            var admin = null;
            function isAdmin(done) {
                if (admin !== null) { return done(admin); }
                var api = window.ApiClient;
                if (!api || !api.getCurrentUser) { return done(false); }
                api.getCurrentUser().then(function (user) {
                    admin = !!(user && user.Policy && user.Policy.IsAdministrator);
                    done(admin);
                }, function () { done(false); });
            }
            function add() {
                if (document.getElementById('tagsmith-user-menu-link')) { return; }
                var settings = document.querySelector('#app-user-menu a[href="#/mypreferencesmenu"]');
                if (!settings) { return; }
                isAdmin(function (allowed) {
                    if (!allowed || document.getElementById('tagsmith-user-menu-link')) { return; }
                    var item = settings.cloneNode(true);
                    item.id = 'tagsmith-user-menu-link';
                    item.href = '#/configurationpage?name=Tagsmith';
                    var icon = item.querySelector('svg');
                    if (icon) {
                        var glyph = document.createElement('span');
                        glyph.className = 'material-icons';
                        glyph.setAttribute('aria-hidden', 'true');
                        glyph.textContent = 'local_offer';
                        icon.parentNode.replaceChild(glyph, icon);
                    }
                    var text = item.querySelector('.MuiListItemText-primary');
                    if (text) { text.textContent = 'Tagsmith'; }
                    item.addEventListener('click', function () {
                        var menu = document.getElementById('app-user-menu');
                        var backdrop = menu && menu.querySelector(':scope > .MuiBackdrop-root');
                        if (backdrop) { backdrop.click(); }
                    });
                    settings.parentNode.insertBefore(item, settings.nextSibling);
                });
            }
            if (window.MutationObserver) {
                new MutationObserver(add).observe(document.body || document.documentElement, { childList: true });
            }
            add();
        })();
        """;

    /// <summary>
    /// Inserts the script into an <c>index.html</c> document. Idempotent.
    /// </summary>
    public static string Inject(string html) => LibraryLinkScript.InjectBlock(html, Marker, Script);
}
