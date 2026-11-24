using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>A channel's name: trimmed, Unicode NFC, 1 to <see cref="MaxLength"/> characters, no control characters.</summary>
internal static class ChannelName
{
    public const int MaxLength = 80;

    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? name)
    {
        name = null;
        if (raw is null)
        {
            return false;
        }

        // Trim() and Normalize() are culture-independent.
        var candidate = raw.Trim();
        if (candidate.Length is 0 or > MaxLength * 2 || !TextRules.IsClean(candidate, allowLayoutControls: false))
        {
            return false;
        }

        candidate = candidate.Normalize(NormalizationForm.FormC);
        if (candidate.Length > MaxLength)
        {
            return false;
        }

        name = candidate;
        return true;
    }

    /// <summary>
    /// What two names are compared by: case-insensitive with the invariant culture, never the current one (under
    /// Turkish rules an upper-case I would become a dotless i), so the result does not depend on where the cell runs.
    /// </summary>
    public static string KeyOf(string name) => name.ToLowerInvariant();
}
