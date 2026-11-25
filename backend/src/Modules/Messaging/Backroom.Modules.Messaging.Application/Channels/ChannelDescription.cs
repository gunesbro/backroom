namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>
/// A channel description (docs/contracts/phase-2.md, flow 1): at most <see cref="MaxLength"/> characters under the
/// <see cref="DisplayText"/> rules without the visible-character requirement. Nothing, or only white space, means no
/// description.
/// </summary>
internal static class ChannelDescription
{
    public const int MaxLength = 250;

    public static bool TryNormalize(string? raw, out string? description)
    {
        description = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DisplayText.TryNormalize(raw, MaxLength, requireVisible: false, out var normalized))
        {
            return false;
        }

        description = normalized;
        return true;
    }
}
