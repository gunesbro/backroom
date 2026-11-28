using System.Text;
using Backroom.BuildingBlocks.Domain.Text;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The emoji of a reaction (docs/contracts/phase-2.md, flow 2 "Reactions"): exactly one emoji sequence (the same rule as an account
/// status, <see cref="EmojiSequence"/>, which the web picker's list is tested against), in NFC, stored in the picker's spelling
/// (<see cref="PickerEmojiList.Canonical"/>: with or without U+FE0F it is one reaction), at most 32 bytes in UTF-8 in that spelling.
/// A few listed sequences (families with skin tones) are longer than that and cannot be used for a reaction.
/// </summary>
internal static class ReactionEmoji
{
    public const int MaxUtf8Bytes = 32;

    public static bool TryRead(string? input, out string emoji)
    {
        emoji = string.Empty;
        if (string.IsNullOrEmpty(input) || !EmojiSequence.TryRead(input, out var read) || read is null)
        {
            return false;
        }

        var canonical = PickerEmojiList.Canonical(read);
        if (Encoding.UTF8.GetByteCount(canonical) > MaxUtf8Bytes)
        {
            return false;
        }

        emoji = canonical;
        return true;
    }
}
