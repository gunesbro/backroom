using System.Globalization;
using System.Text;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Domain.Text;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// The rules of a manual status (docs/contracts/phase-2.md, flow 9). Do not disturb ends in the future and within
/// <see cref="MaxDndAhead"/>. A custom status has one emoji, a text of at most <see cref="MaxTextLength"/> user-perceived characters,
/// or both, and an optional end in the future within <see cref="MaxCustomAhead"/>. The text is NFC and trimmed and has no control,
/// line separator, private-use, unassigned or format characters except the joiners between two characters (no bidi overrides or
/// invisible padding), like Directory's display names. The emoji is a single grapheme cluster made only of emoji code points. The
/// time zone is an IANA id the runtime knows. Every failure is the same <see cref="IdentityErrors.StatusInvalid"/>. Away chosen by
/// hand has no end and gives way to do not disturb: a status with do not disturb is stored with away cleared, so away does not come
/// back when do not disturb ends (owner decision of 2026-11-25).
/// </summary>
internal static class AccountStatusRules
{
    public const int MaxTextLength = 100;

    /// <summary>Code points per user-perceived character on average (the database checks this times the length).</summary>
    public const int MaxCodePointsPerCharacter = 4;

    private const int MaxCodePointsInOneCharacter = 10;

    public const int MaxTimeZoneLength = 64;

    public static readonly TimeSpan MaxDndAhead = TimeSpan.FromDays(7);

    public static readonly TimeSpan MaxCustomAhead = TimeSpan.FromDays(30);

    /// <summary>Validates and normalizes a status; <paramref name="hasCustomStatus"/> is false when the request carries none.</summary>
    public static Result<AccountStatusInput> Validate(
        DateTimeOffset? dndUntil,
        bool away,
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

        away = away && dndUntil is null;
        if (!hasCustomStatus)
        {
            return new AccountStatusInput(dndUntil, null, null, null, timeZone!, away);
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

        return new AccountStatusInput(dndUntil, emoji, text, customExpiresAt, timeZone!, away);
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
    /// One emoji: a single grapheme cluster of emoji code points (<see cref="EmojiSequence.TryRead"/>).
    /// </summary>
    public static bool TryReadEmoji(string input, out string? emoji) => EmojiSequence.TryRead(input, out emoji);

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
