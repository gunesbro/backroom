using System.Globalization;
using System.Security.Claims;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>What the revocation check needs from an authenticated access token (Keycloak's claim names, no inbound mapping).</summary>
internal readonly record struct TokenIdentity(Guid AccountId, string? SessionId, DateTimeOffset IssuedAt)
{
    public static bool TryRead(ClaimsPrincipal? user, out TokenIdentity identity)
    {
        identity = default;
        if (user?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(user.FindFirst("sub")?.Value, out var accountId) || accountId == Guid.Empty)
        {
            return false;
        }

        // A token without a readable iat is treated as issued at the start of time, so any revocation of the account covers it.
        var issuedAt = long.TryParse(user.FindFirst("iat")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 0 and <= 253_402_300_799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UnixEpoch;
        identity = new TokenIdentity(accountId, user.FindFirst("sid")?.Value, issuedAt);
        return true;
    }
}
