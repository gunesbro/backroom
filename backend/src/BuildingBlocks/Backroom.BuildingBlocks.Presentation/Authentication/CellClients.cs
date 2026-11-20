using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>A cell's Keycloak client is <c>cell-{cellId}</c> (docs/contracts/phase-1.md, "Cell registration").</summary>
public static class CellClients
{
    private const string Prefix = "cell-";

    /// <summary>The cell id of a token issued to a cell's client (<c>azp</c>), validated by the cell scheme.</summary>
    public static bool TryGetCellId(ClaimsPrincipal principal, [NotNullWhen(true)] out string? cellId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return TryParse(principal.FindFirst(KeycloakClaims.AuthorizedParty)?.Value, out cellId);
    }

    internal static bool TryParse(string? clientId, [NotNullWhen(true)] out string? cellId)
    {
        cellId = null;
        if (clientId is null || !clientId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = clientId[Prefix.Length..];
        if (!Audiences.IsValidCellId(id))
        {
            return false;
        }

        cellId = id;
        return true;
    }
}
