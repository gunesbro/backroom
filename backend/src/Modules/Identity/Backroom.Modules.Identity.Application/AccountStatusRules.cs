using System.Globalization;
using System.Text;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// The rules of a manual status (docs/contracts/phase-2.md, flow 9). Do not disturb ends in the future and within
/// <see cref="MaxDndAhead"/>. A custom status has one emoji, a text of at most <see cref="MaxTextLength"/> user-perceived characters,
/// or both, and an optional end in the future within <see cref="MaxCustomAhead"/>. The text is NFC and trimmed and has no control,
/// line separator, private-use, unassigned or format characters except the joiners between two characters (no bidi overrides or
/// invisible padding), like Directory's display names. The emoji is a single grapheme cluster made only of emoji code points. The
/// time zone is an IANA id the runtime knows. Every failure is the same <see cref="IdentityErrors.StatusInvalid"/>.
/// </summary>
internal static class AccountStatusRules
{
    public const int MaxTextLength = 100;

    /// <summary>Code points per user-perceived character on average (the database checks this times the length).</summary>
    public const int MaxCodePointsPerCharacter = 4;

    private const int MaxCodePointsInOneCharacter = 10;

    /// <summary>The longest emoji sequence accepted (a family with skin tones, a subdivision flag), in code points.</summary>
    public const int MaxEmojiCodePoints = 16;

    public const int MaxTimeZoneLength = 64;

    public static readonly TimeSpan MaxDndAhead = TimeSpan.FromDays(7);

    public static readonly TimeSpan MaxCustomAhead = TimeSpan.FromDays(30);

    /// <summary>Validates and normalizes a status; <paramref name="hasCustomStatus"/> is false when the request carries none.</summary>
    public static Result<AccountStatusInput> Validate(
        DateTimeOffset? dndUntil,
        bool hasCustomStatus,
        string? customEmoji,
        string? customText,
        DateTimeOffset? customExpiresAt,
        string? timeZone,
        DateTimeOffset now)
    {
        if (!IsTimeZone(timeZone))
        {
            return IdentityErrors.TimeZoneInvalid;
        }

        if (dndUntil is { } dnd && (dnd <= now || dnd - now > MaxDndAhead))
        {
            return IdentityErrors.StatusInvalid;
        }

        if (!hasCustomStatus)
        {
            return new AccountStatusInput(dndUntil, null, null, null, timeZone!);
        }

        string? emoji = null;
        string? text = null;
        if ((!string.IsNullOrEmpty(customEmoji) && !TryReadEmoji(customEmoji, out emoji))
            || (!string.IsNullOrEmpty(customText) && !TryNormalizeText(customText, out text))
            || (emoji is null && text is null)
            || (customExpiresAt is { } end && (end <= now || end - now > MaxCustomAhead)))
        {
            return IdentityErrors.StatusInvalid;
        }

        return new AccountStatusInput(dndUntil, emoji, text, customExpiresAt, timeZone!);
    }

    /// <summary>The status as it stands at <paramref name="now"/>: expired parts are left out.</summary>
    public static AccountStatus Effective(AccountStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        var customLive = (status.CustomEmoji is not null || status.CustomText is not null)
            && (status.CustomExpiresAt is null || status.CustomExpiresAt > now);
        return status with
        {
            DndUntil = status.DndUntil > now ? status.DndUntil : null,
            CustomEmoji = customLive ? status.CustomEmoji : null,
            CustomText = customLive ? status.CustomText : null,
            CustomExpiresAt = customLive ? status.CustomExpiresAt : null,
        };
    }

    public static bool IsTimeZone(string? timeZone)
    {
        if (string.IsNullOrEmpty(timeZone) || timeZone.Length > MaxTimeZoneLength)
        {
            return false;
        }

        foreach (var character in timeZone)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+'))
            {
                return false;
            }
        }

        // An IANA id (Windows ids have spaces and are refused above) that the runtime knows (ICU on Windows, tzdata on Linux).
        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _)
            && (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _) || string.Equals(timeZone, "UTC", StringComparison.Ordinal));
    }

    /// <summary>
    /// One emoji: a single grapheme cluster that is a flag (two regional indicators), a keycap (digit, # or * with U+20E3), a
    /// subdivision flag (black flag and tag characters ending in U+E007F), or emoji characters (<see cref="IsEmojiBase"/>) with skin
    /// tones and variation selectors, joined by ZWJ. Letters, symbols such as ©, box drawing and braille are refused.
    /// </summary>
    public static bool TryReadEmoji(string input, out string? emoji)
    {
        emoji = null;
        if (input.Length > MaxEmojiCodePoints * 2 || !TryNormalize(input, out var value))
        {
            return false;
        }

        // Exactly one grapheme cluster (UAX #29 keeps ZWJ sequences, flags, keycaps and skin tones together).
        var elements = StringInfo.GetTextElementEnumerator(value);
        if (!elements.MoveNext() || elements.GetTextElement().Length != value.Length)
        {
            return false;
        }

        var runes = value.EnumerateRunes().ToList();
        if (runes.Count > MaxEmojiCodePoints)
        {
            return false;
        }

        if (IsRegionalIndicator(runes[0]))
        {
            return runes.Count == 2 && IsRegionalIndicator(runes[1]) && Accept(value, out emoji);
        }

        if (runes[0].Value is (>= '0' and <= '9') or '#' or '*')
        {
            var keycap = runes.Count == 3 && runes[1].Value == 0xFE0F && runes[2].Value == 0x20E3
                || runes.Count == 2 && runes[1].Value == 0x20E3;
            return keycap && Accept(value, out emoji);
        }

        if (runes[0].Value == 0x1F3F4 && runes.Count > 2 && runes[1].Value is >= 0xE0020 and <= 0xE007E)
        {
            var tags = runes.Skip(1).ToList();
            return tags[^1].Value == 0xE007F && tags.SkipLast(1).All(rune => rune.Value is >= 0xE0020 and <= 0xE007E) && Accept(value, out emoji);
        }

        // Emoji characters joined by ZWJ, each optionally followed by a skin tone and a variation selector.
        var expectBase = true;
        foreach (var rune in runes)
        {
            if (expectBase)
            {
                if (!IsEmojiBase(rune))
                {
                    return false;
                }

                expectBase = false;
            }
            else if (rune.Value == 0x200D)
            {
                expectBase = true;
            }
            else if (rune.Value is not ((>= 0x1F3FB and <= 0x1F3FF) or 0xFE0F))
            {
                return false;
            }
        }

        return !expectBase && Accept(value, out emoji);
    }

    private static bool Accept(string value, out string? emoji)
    {
        emoji = value;
        return true;
    }

    private static bool IsRegionalIndicator(Rune rune) => rune.Value is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>
    /// A bounded table of the code points that are emoji on their own (Unicode's Emoji property, without the digits, ©, ® and ™ that
    /// need a variation selector to look like one): the pictograph planes and the emoji in the symbol blocks of the first plane.
    /// </summary>
    internal static bool IsEmojiBase(Rune rune) => rune.Value switch
    {
        >= 0x1F000 and <= 0x1F02F => rune.Value == 0x1F004,
        >= 0x1F0A0 and <= 0x1F0FF => rune.Value == 0x1F0CF,
        >= 0x1F170 and <= 0x1F1FF => rune.Value is 0x1F170 or 0x1F171 or 0x1F17E or 0x1F17F or 0x1F18E or (>= 0x1F191 and <= 0x1F19A),
        >= 0x1F200 and <= 0x1F2FF => rune.Value is 0x1F201 or 0x1F202 or 0x1F21A or 0x1F22F or (>= 0x1F232 and <= 0x1F23A) or 0x1F250 or 0x1F251,
        >= 0x1F3FB and <= 0x1F3FF => false,
        >= 0x1F300 and <= 0x1F64F => true,
        >= 0x1F680 and <= 0x1F6FF => true,
        >= 0x1F7E0 and <= 0x1F7EB => true,
        0x1F7F0 => true,
        >= 0x1F90C and <= 0x1F9FF => true,
        >= 0x1FA70 and <= 0x1FAFF => true,
        0x203C or 0x2049 or 0x2139 or (>= 0x2194 and <= 0x2199) or 0x21A9 or 0x21AA => true,
        0x231A or 0x231B or 0x2328 or 0x23CF or (>= 0x23E9 and <= 0x23F3) or (>= 0x23F8 and <= 0x23FA) or 0x24C2 => true,
        0x25AA or 0x25AB or 0x25B6 or 0x25C0 or (>= 0x25FB and <= 0x25FE) => true,
        >= 0x2600 and <= 0x27BF => IsSymbolEmoji(rune.Value),
        0x2934 or 0x2935 or (>= 0x2B05 and <= 0x2B07) or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
        0x3030 or 0x303D or 0x3297 or 0x3299 => true,
        _ => false,
    };

    // The emoji of Miscellaneous Symbols and Dingbats (most of those blocks are plain symbols).
    private static bool IsSymbolEmoji(int value) => value is
        (>= 0x2600 and <= 0x2604) or 0x260E or 0x2611 or 0x2614 or 0x2615 or 0x2618 or 0x261D or 0x2620 or 0x2622 or 0x2623
        or 0x2626 or 0x262A or 0x262E or 0x262F or (>= 0x2638 and <= 0x263A) or 0x2640 or 0x2642 or (>= 0x2648 and <= 0x2653)
        or 0x265F or 0x2660 or 0x2663 or 0x2665 or 0x2666 or 0x2668 or 0x267B or 0x267E or 0x267F or (>= 0x2692 and <= 0x2697)
        or 0x2699 or 0x269B or 0x269C or 0x26A0 or 0x26A1 or 0x26A7 or 0x26AA or 0x26AB or 0x26B0 or 0x26B1 or 0x26BD or 0x26BE
        or 0x26C4 or 0x26C5 or 0x26C8 or 0x26CE or 0x26CF or 0x26D1 or 0x26D3 or 0x26D4 or 0x26E9 or 0x26EA or (>= 0x26F0 and <= 0x26F5)
        or (>= 0x26F7 and <= 0x26FA) or 0x26FD or 0x2702 or 0x2705 or (>= 0x2708 and <= 0x270D) or 0x270F or 0x2712 or 0x2714
        or 0x2716 or 0x271D or 0x2721 or 0x2728 or 0x2733 or 0x2734 or 0x2744 or 0x2747 or 0x274C or 0x274E or (>= 0x2753 and <= 0x2755)
        or 0x2757 or 0x2763 or 0x2764 or (>= 0x2795 and <= 0x2797) or 0x27A1 or 0x27B0 or 0x27BF;

    public static bool TryNormalizeText(string input, out string? text)
    {
        text = null;
        if (input.Length > MaxTextLength * MaxCodePointsPerCharacter * 2 || !TryNormalize(input, out var normalized))
        {
            return false;
        }

        var value = normalized.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var codePoints = 0;
        var previousWasJoiner = true; // A joiner may not lead.
        foreach (var rune in value.EnumerateRunes())
        {
            codePoints++;
            var category = Rune.GetUnicodeCategory(rune);
            var joiner = rune.Value is 0x200C or 0x200D;
            var refused = category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                || (category is UnicodeCategory.Format && !joiner)
                // Letters and marks that render as blanks (Hangul fillers, Khmer inherent vowels, the blank braille cell).
                || rune.Value is 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0;
            if (refused || (joiner && previousWasJoiner))
            {
                return false;
            }

            previousWasJoiner = joiner;
        }

        // A joiner may not trail either.
        if (previousWasJoiner || codePoints > MaxTextLength * MaxCodePointsPerCharacter)
        {
            return false;
        }

        var characters = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            if (++characters > MaxTextLength || elements.GetTextElement().EnumerateRunes().Count() > MaxCodePointsInOneCharacter)
            {
                return false;
            }
        }

        text = value;
        return true;
    }

    private static bool TryNormalize(string input, out string value)
    {
        try
        {
            value = input.Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            // Ill-formed UTF-16 (a lone surrogate).
            value = string.Empty;
            return false;
        }
    }
}
