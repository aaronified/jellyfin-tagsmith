using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// Smooths over the language-code quirks of the sources Tagsmith reads, so everything
/// downstream can hand a code to <c>ILocalizationManager.FindLanguageInfo</c> and expect
/// a sensible tag value back.
/// </summary>
public static class LanguageCodes
{
    private static readonly Regex _parenthetical = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex _whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Reduces an ISO 639-2 display name to the language people would name, so the tag is
    /// <c>lang=spanish</c> rather than <c>lang=spanish_castilian</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's table carries scholarly headings, not everyday names, and 43 of the
    /// languages with a two-letter code have one: <c>Spanish; Castilian</c>,
    /// <c>Dutch; Flemish</c>, <c>Romanian; Moldavian; Moldovan</c>,
    /// <c>Chinese (Traditional)</c>, <c>Greek, Modern (1453-)</c>. Tagging those verbatim
    /// splits one language across several values, and none of the values is what anyone
    /// would search for or name an artwork file.
    /// </para>
    /// <para>Three rules, applied in order, each matching a convention of the source table:</para>
    /// <list type="number">
    /// <item><description>
    /// <b>Semicolons separate synonyms</b>, most common first — so keep the first and drop
    /// the rest. <c>Spanish; Castilian</c> becomes <c>Spanish</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Parentheses hold qualifiers</b> — a date range, a script, a region. Dropping them
    /// is what folds <c>Chinese (Traditional)</c>, <c>Portuguese (Brazil)</c> and
    /// <c>Norwegian (Bokmal)</c> onto one collection each, which is the same choice
    /// <see cref="Normalise"/> already makes for the codes.
    /// </description></item>
    /// <item><description>
    /// <b>A comma marks an inverted heading</b>, so swap the halves rather than truncating:
    /// <c>Greek, Modern</c> is <c>Modern Greek</c>. Truncating would read as the right
    /// answer on that one and collapse <c>Ndebele, South</c> and <c>Ndebele, North</c> —
    /// two different languages — onto a single tag.
    /// </description></item>
    /// </list>
    /// <para>
    /// Applied across the whole 10.11.11 table, the only names that end up shared are the
    /// script, region and dialect variants that should be shared: Chinese, French,
    /// Norwegian, Portuguese and Spanish. Nothing else collides.
    /// </para>
    /// </remarks>
    /// <param name="displayName">The name the server's localisation table gave.</param>
    /// <returns>The simplified name, or the input unchanged if it reduces to nothing.</returns>
    public static string Simplify(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        var name = displayName;

        var synonym = name.IndexOf(';', StringComparison.Ordinal);
        if (synonym >= 0)
        {
            name = name[..synonym];
        }

        name = Tidy(_parenthetical.Replace(name, " "));

        // A single comma is an inverted heading and swaps. Two or more is a list, and the
        // only row in the table with two survives the semicolon rule long before it gets
        // here, so this branch is belt and braces rather than a case that fires.
        //
        // The swap is not universally flattering: "Creoles and pidgins, English based"
        // becomes "English based Creoles and pidgins". Those three rows carry no two-letter
        // code, and LocalizationManager.LoadCultures skips any row that does not, so
        // FindLanguageInfo can never return them — see the test.
        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && name.IndexOf(',', comma + 1) < 0)
        {
            var head = name[..comma].Trim();
            var tail = name[(comma + 1)..].Trim();

            if (head.Length > 0 && tail.Length > 0)
            {
                name = Tidy(tail + " " + head);
            }
        }

        // A name that reduces to nothing is not a simplification. Better a clumsy tag than
        // no tag, and better still to notice the table changed shape.
        return name.Length == 0 ? displayName.Trim() : name;
    }

    private static string Tidy(string value) => _whitespace.Replace(value, " ").Trim(' ', ',');

    /// <summary>
    /// Returns true for codes that mean "there is no language to tag": TMDb uses
    /// <c>xx</c> for no linguistic content and ISO 639-2 reserves <c>zxx</c> for the same.
    /// A silent film should carry no language tag, not <c>lang=xx</c>.
    /// </summary>
    public static bool IsNoLanguage(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var trimmed = code.Trim();
        return trimmed.Equals("xx", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("zxx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name to use for a code where the server's answer is missing, or is right but not
    /// what anybody calls the language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TMDb uses <c>cn</c> for Cantonese, and the server's ISO 639-2 table has no Cantonese
    /// row (nor a <c>yue</c> one) to resolve it against.
    /// </para>
    /// <para>
    /// Greek is the one place <see cref="Simplify"/>'s comma rule, which is right in
    /// general, lands somewhere nobody would type. <c>Greek, Modern (1453-)</c> correctly
    /// de-inverts to "Modern Greek" — but <c>el</c> is the only Greek the server can
    /// return, since <c>grc</c> has no two-letter code and so never resolves, which makes
    /// the qualifier pure noise. Every Greek film would tag <c>lang=modern_greek</c>.
    /// </para>
    /// </remarks>
    public static string? DisplayOverride(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return code.Trim().ToLowerInvariant() switch
        {
            "cn" or "yue" => "Cantonese",
            "el" or "ell" or "gre" => "Greek",
            _ => null
        };
    }

    /// <summary>
    /// Maps a source's language code onto one Jellyfin's localisation tables know.
    /// </summary>
    /// <remarks>
    /// The quirk codes are TVDb's — <c>zhtw</c> is its private id for Traditional Chinese
    /// and <c>por</c> its European Portuguese, next to <c>pt</c> for Brazilian. The
    /// server's tables could keep those distinctions (<c>zh-tw</c>, <c>pt-pt</c> and
    /// <c>pt-br</c> all have rows), but a Languages library does not want them: one
    /// Chinese and one Portuguese collection, not one per script or region, so the
    /// variants deliberately fold to the base language.
    /// </remarks>
    public static string Normalise(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var trimmed = code.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "zhtw" => "zh",
            "por" => "pt",
            _ => trimmed
        };
    }
}
