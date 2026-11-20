using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Backroom.Modules.Email.Application.Templates;

/// <summary>How untrusted names (an organization's name, later people's names) go into an email.</summary>
internal static class EmailText
{
    /// <summary>Characters of a name in a sender name, subject or body; longer names are cut and end with "…".</summary>
    public const int MaxNameLength = 60;

    /// <summary>U+2060 WORD JOINER: invisible, and it breaks the patterns mail clients turn into links.</summary>
    private const char WordJoiner = '\u2060';

    private const int ZeroWidthNonJoiner = 0x200C;
    private const int ZeroWidthJoiner = 0x200D;

    // Letters of every script stay readable; markup characters and quotes are always encoded.
    private static readonly HtmlEncoder s_html = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// One line of visible text: control characters, line breaks and other white space become single spaces;
    /// invisible format characters (bidirectional overrides that could disguise a name, zero-width spaces) are
    /// removed, except a zero-width joiner or non-joiner between two letters, marks or symbols, which some scripts
    /// (Persian, Indic) and emoji sequences need; the result is trimmed and cut at <see cref="MaxNameLength"/>
    /// characters without splitting a character. <paramref name="fallback"/> if nothing visible is left.
    /// </summary>
    public static string CleanName(string value, string fallback)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(fallback);

        // Enough runes for the longest name that is kept, plus the "…".
        var runes = new List<Rune>(Math.Min(value.Length, MaxNameLength * 4));
        foreach (var rune in value.EnumerateRunes())
        {
            if (runes.Count == MaxNameLength * 4)
            {
                break;
            }

            runes.Add(rune);
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxNameLength * 2));
        var pendingSpace = false;
        Span<char> buffer = stackalloc char[2];
        for (var index = 0; index < runes.Count; index++)
        {
            var rune = runes[index];
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format && !IsJoinerBetweenLetters(runes, index))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(buffer[..rune.EncodeToUtf16(buffer)]);
        }

        var cleaned = builder.ToString();
        return cleaned.Length == 0 ? fallback : Truncate(cleaned);
    }

    /// <summary>
    /// A cleaned name for a header (sender name, subject): <c>@</c> becomes a space, so a name such as
    /// <c>security@bank.example</c> cannot pass for an address in the sender name, and it is kept from becoming a link.
    /// </summary>
    public static string HeaderName(string value, string fallback)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WithoutLinks(CleanName(value.Replace('@', ' '), fallback));
    }

    /// <summary>
    /// Inserts a word joiner after '.', '@', ':' and '/', so a name such as <c>example.com</c> or
    /// <c>https://example.com</c> stays text in clients that turn addresses into links.
    /// </summary>
    public static string WithoutLinks(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            builder.Append(character);
            if (character is '.' or '@' or ':' or '/')
            {
                builder.Append(WordJoiner);
            }
        }

        return builder.ToString();
    }

    public static string Html(string value) => s_html.Encode(value);

    /// <summary>
    /// The translation with <c>{product}</c> and then <c>{organization}</c> filled in, in that order, so a name that
    /// contains a placeholder is never expanded.
    /// </summary>
    public static string Fill(string template, string organization)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template
            .Replace("{product}", EmailBranding.ProductName, StringComparison.Ordinal)
            .Replace("{organization}", organization, StringComparison.Ordinal);
    }

    private static bool IsJoinerBetweenLetters(List<Rune> runes, int index) =>
        runes[index].Value is ZeroWidthJoiner or ZeroWidthNonJoiner
        && index > 0 && index < runes.Count - 1
        && IsJoinable(runes[index - 1]) && IsJoinable(runes[index + 1]);

    private static bool IsJoinable(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol;

    private static string Truncate(string value)
    {
        if (value.Length <= MaxNameLength)
        {
            return value;
        }

        var end = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + elements.GetTextElement().Length;
            if (next > MaxNameLength - 1)
            {
                break;
            }

            end = next;
        }

        return string.Concat(value.AsSpan(0, end).TrimEnd(), "…");
    }
}
