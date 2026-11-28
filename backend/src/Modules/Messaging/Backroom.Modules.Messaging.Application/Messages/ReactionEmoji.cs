using System.Text;
using Backroom.BuildingBlocks.Domain.Text;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The emoji of a reaction (docs/contracts/phase-2.md, flow 2 "Reactions"): exactly one emoji sequence (the same rule as an account
/// status, <see cref="EmojiSequence"/>, which the web picker's list is tested against), at most 32 bytes in UTF-8, in NFC.
/// </summary>
internal static class ReactionEmoji
{
    public const int MaxUtf8Bytes = 32;

    public static bool TryRead(string? input, out string emoji)
    {
        emoji = string.Empty;
        if (string.IsNullOrEmpty(input) || !EmojiSequence.TryRead(input, out var read) || read is null
            || Encoding.UTF8.GetByteCount(read) > MaxUtf8Bytes)
        {
            return false;
        }

        emoji = read;
        return true;
    }
}
