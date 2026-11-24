using System.Diagnostics.CodeAnalysis;

namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>
/// A channel name: the same rule as other names people see (<see cref="DisplayText"/>: NFC, trimmed, a visible
/// character, no control or invisible format characters), 1 to <see cref="MaxLength"/> characters.
/// </summary>
internal static class ChannelName
{
    public const int MaxLength = 80;

    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? name)
    {
        var valid = DisplayText.TryNormalize(raw, MaxLength, out var normalized);
        name = valid ? normalized : null;
        return valid;
    }

    /// <summary>
    /// What two names are compared by: case-insensitive with the invariant culture, never the current one (under
    /// Turkish rules an upper-case I would become a dotless i), so the result does not depend on where the cell runs.
    /// </summary>
    public static string KeyOf(string name) => name.ToLowerInvariant();
}
