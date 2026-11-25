using System.Globalization;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// The rules of <see cref="PresenceScripts.Evaluate"/> for readers (the same state from the same fields), and the stored formats:
/// a connection is <c>{expiresAt}:{lastActiveAt}:{idle 0|1}</c> in Unix milliseconds; status timestamps are Unix milliseconds or empty.
/// </summary>
internal static class PresenceRules
{
    public static string Connection(long expiresAtMs, long lastActiveAtMs, bool idle) =>
        string.Create(CultureInfo.InvariantCulture, $"{expiresAtMs}:{lastActiveAtMs}:{(idle ? 1 : 0)}");

    /// <summary>The state of a person from their connection fields at <paramref name="nowMs"/>.</summary>
    public static PresenceState StateOf(IEnumerable<string?> connections, long nowMs, long idleAfterMs)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var state = PresenceState.Offline;
        foreach (var value in connections)
        {
            if (!TryParse(value, out var expiresAt, out var lastActiveAt, out var idle) || expiresAt <= nowMs)
            {
                continue;
            }

            var current = !idle && lastActiveAt + idleAfterMs > nowMs ? PresenceState.Online : PresenceState.Idle;
            if (current > state)
            {
                state = current;
            }
        }

        return state;
    }

    /// <summary>
    /// The state others see: away chosen by hand (<paramref name="away"/>, the stored flag) shows an online person as idle. Offline
    /// stays offline, and idle is idle either way, so nobody can tell the two apart.
    /// </summary>
    public static PresenceState Shown(PresenceState state, string? away) =>
        state == PresenceState.Online && away == "1" ? PresenceState.Idle : state;

    public static string StateName(PresenceState state) => state switch
    {
        PresenceState.Online => "online",
        PresenceState.Idle => "idle",
        _ => "offline",
    };

    /// <summary>Do not disturb in force at <paramref name="nowMs"/>, from the stored value.</summary>
    public static DateTimeOffset? DndUntil(string? stored, long nowMs) =>
        TryMilliseconds(stored, out var until) && until > nowMs ? DateTimeOffset.FromUnixTimeMilliseconds(until) : null;

    /// <summary>The custom status in force at <paramref name="nowMs"/>, from the stored values.</summary>
    public static CustomStatus? Custom(string? emoji, string? text, string? expiresAt, long nowMs)
    {
        var hasEmoji = !string.IsNullOrEmpty(emoji);
        var hasText = !string.IsNullOrEmpty(text);
        if (!hasEmoji && !hasText)
        {
            return null;
        }

        DateTimeOffset? end = null;
        if (TryMilliseconds(expiresAt, out var expires))
        {
            if (expires <= nowMs)
            {
                return null;
            }

            end = DateTimeOffset.FromUnixTimeMilliseconds(expires);
        }

        return new CustomStatus(hasEmoji ? emoji : null, hasText ? text : null, end);
    }

    /// <summary>ISO 8601 in UTC with milliseconds, as live events carry timestamps.</summary>
    public static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Milliseconds(DateTimeOffset? value) =>
        value is { } time ? time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static bool TryMilliseconds(string? value, out long milliseconds) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds);

    private static bool TryParse(string? value, out long expiresAt, out long lastActiveAt, out bool idle)
    {
        expiresAt = 0;
        lastActiveAt = 0;
        idle = false;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(':');
        if (parts.Length != 3 || !TryMilliseconds(parts[0], out expiresAt) || !TryMilliseconds(parts[1], out lastActiveAt) || parts[2] is not ("0" or "1"))
        {
            return false;
        }

        idle = parts[2] == "1";
        return true;
    }
}
