namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// Smooths over the language-code quirks of the sources Tagsmith reads, so everything
/// downstream can hand a code to <c>ILocalizationManager.FindLanguageInfo</c> and expect
/// a sensible tag value back.
/// </summary>
public static class LanguageCodes
{
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
    /// The display name for codes Jellyfin's localisation tables cannot resolve at all.
    /// TMDb uses <c>cn</c> for Cantonese, and the server's ISO 639-2 table has no
    /// Cantonese row (nor a <c>yue</c> one) to resolve it against.
    /// </summary>
    public static string? DisplayOverride(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return code.Trim().ToLowerInvariant() switch
        {
            "cn" or "yue" => "Cantonese",
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
