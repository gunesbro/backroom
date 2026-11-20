using System.Globalization;
using System.Text;

namespace Backroom.Modules.Directory.Application;

/// <summary>
/// Names people type and others see (an organization name, a display name), possibly in emails: NFC,
/// trimmed; at most a number of user-perceived characters, at most four code points per character on
/// average and ten in one (against stacked marks), which the database checks too; no control, separator,
/// private-use, surrogate or unassigned characters and no format characters other than the joiners ZWNJ
/// and ZWJ (needed in Persian, Indic scripts and emoji), each only between two other characters, so no
/// bidi overrides, invisible marks or look-alike padding; and at
/// least one visible character, so a name cannot look blank.
/// </summary>
internal static class DisplayText
{
    /// <summary>Code points per user-perceived character on average (the database's limit is this times the length).</summary>
    public const int MaxCodePointsPerCharacter = 4;

    private const int MaxCodePointsInOneCharacter = 10;

    public static bool TryNormalize(string? input, int maxLength, out string normalized)
    {
        normalized = string.Empty;

        // A user-perceived character takes at most a few UTF-16 units; anything far longer is refused before normalizing.
        if (string.IsNullOrWhiteSpace(input) || input.Length > maxLength * MaxCodePointsPerCharacter * 2)
        {
            return false;
        }

        string text;
        try
        {
            text = input.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (ArgumentException)
        {
            // Ill-formed UTF-16 (a lone surrogate).
            return false;
        }

        var codePoints = 0;
        var visible = false;
        var previousWasJoiner = true; // A joiner may not lead.
        foreach (var rune in text.EnumerateRunes())
        {
            codePoints++;
            var category = Rune.GetUnicodeCategory(rune);
            var joiner = IsJoiner(rune);
            if (IsRefused(rune, category) || (joiner && previousWasJoiner))
            {
                return false;
            }

            visible |= IsVisible(rune, category);
            previousWasJoiner = joiner;
        }

        // A joiner may not trail either: it joins two characters or nothing.
        if (!visible || previousWasJoiner || codePoints > maxLength * MaxCodePointsPerCharacter)
        {
            return false;
        }

        var characters = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            if (++characters > maxLength || elements.GetTextElement().EnumerateRunes().Count() > MaxCodePointsInOneCharacter)
            {
                return false;
            }
        }

        normalized = text;
        return true;
    }

    private static bool IsRefused(Rune rune, UnicodeCategory category) =>
        category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
        || (category is UnicodeCategory.Format && !IsJoiner(rune));

    private static bool IsJoiner(Rune rune) => rune.Value is 0x200C or 0x200D;

    // Letters, digits, symbols and punctuation, except characters that render as blanks.
    private static bool IsVisible(Rune rune, UnicodeCategory category) =>
        rune.Value is not (0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0)
        && category is not (UnicodeCategory.SpaceSeparator or UnicodeCategory.Format
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
}
