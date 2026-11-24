using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The account id of a connection is the token's <c>sub</c> (Keycloak's user id). The cell's authentication keeps
/// Keycloak's claim names, so SignalR's default (<c>NameIdentifier</c>) would find nothing.
/// </summary>
internal sealed class AccountUserIdProvider : IUserIdProvider
{
    private const string SubjectClaim = "sub";

    public string? GetUserId(HubConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return TryGetAccountId(connection.User, out var accountId) ? accountId.ToString("D") : null;
    }

    public static bool TryGetAccountId(ClaimsPrincipal? user, out Guid accountId)
    {
        accountId = default;
        return Guid.TryParse(user?.FindFirst(SubjectClaim)?.Value, out accountId) && accountId != Guid.Empty;
    }
}
