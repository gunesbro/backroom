using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// A file's display name (docs/contracts/phase-2.md, flow 6, step 1): NFC, trimmed, 1 to 255 characters (code points,
/// as PostgreSQL's <c>char_length</c> counts), no path separators, no control or format characters (so no bidirectional
/// overrides that disguise an extension) except ZWNJ and ZWJ, and not <c>.</c> or <c>..</c>. The name is stored
/// for display only: it never appears in storage keys, URLs, logs or events.
/// </summary>
internal static class FileName
{
    public const int MaxLength = 255;

    private const char ZeroWidthNonJoiner = '‌';
    private const char ZeroWidthJoiner = '‍';

    public static bool TryNormalize(string? name, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxLength * 2 + 16)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = name.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates and other invalid code points.
            return false;
        }

        if (candidate.Length == 0 || candidate is "." or ".." || !IsWellFormed(candidate, out var codePoints) || codePoints > MaxLength)
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    private static bool IsWellFormed(string candidate, out int codePoints)
    {
        codePoints = 0;
        foreach (var rune in candidate.EnumerateRunes())
        {
            codePoints++;
            if (rune.Value is '/' or '\\' or 0xFFFD)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || (category == UnicodeCategory.Format && rune.Value is not (ZeroWidthNonJoiner or ZeroWidthJoiner)))
            {
                return false;
            }
        }

        return true;
    }
}
