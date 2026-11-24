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
            return IdentityErrors.StatusInvalid;
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

        var count = 0;
        var keycapBase = false;
        var keycap = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            bool allowed;
            if (count == 0)
            {
                keycapBase = rune.Value is (>= '0' and <= '9') or '#' or '*';
                allowed = category == UnicodeCategory.OtherSymbol || keycapBase;
            }
            else
            {
                keycap |= rune.Value == 0x20E3;
                allowed = category == UnicodeCategory.OtherSymbol
                    || rune.Value is (>= 0x1F3FB and <= 0x1F3FF) or 0x200D or 0xFE0F or 0x20E3 or (>= 0xE0020 and <= 0xE007F);
            }

            if (!allowed || ++count > MaxEmojiCodePoints)
            {
                return false;
            }
        }

        // A digit, # or * is an emoji only as a keycap.
        if (keycapBase && !keycap)
        {
            return false;
        }

        emoji = value;
        return true;
    }

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
                || (category is UnicodeCategory.Format && !joiner);
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
