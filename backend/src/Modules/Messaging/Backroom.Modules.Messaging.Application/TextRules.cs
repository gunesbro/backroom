namespace Backroom.Modules.Messaging.Application;

/// <summary>Character rules shared by channel names and message text.</summary>
internal static class TextRules
{
    /// <summary>
    /// Whether the text holds only well-formed UTF-16 (no lone surrogates, which the database cannot store) and no
    /// control characters, line or paragraph separators; a message may also hold line feeds, carriage returns and tabs.
    /// </summary>
    public static bool IsClean(string text, bool allowLayoutControls)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
            else if (allowLayoutControls && character is '\n' or '\r' or '\t')
            {
                continue;
            }
            else if (char.IsControl(character) || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }
}
