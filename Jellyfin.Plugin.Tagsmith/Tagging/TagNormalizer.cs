using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Tagsmith.Tagging;

/// <summary>
/// Turns free-form metadata values into stable, machine-readable tag fragments.
/// </summary>
public static class TagNormalizer
{
    /// <summary>
    /// Lowercases, strips diacritics and collapses anything non-alphanumeric to underscores.
    /// </summary>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                }

                pendingSeparator = false;
                builder.Append(c);
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Builds a full tag, e.g. <c>origin=india</c>. Returns null when the value is empty.
    /// </summary>
    public static string? Compose(string tagNamespace, string separator, string? value)
    {
        var slug = Slug(value);
        return slug.Length == 0 ? null : string.Concat(tagNamespace, separator, slug);
    }
}
