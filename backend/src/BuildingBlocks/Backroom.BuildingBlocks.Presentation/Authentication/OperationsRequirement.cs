using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>
/// A token of the operations panel client with realm role <c>ops</c> and two-factor authentication;
/// see <see cref="AuthorizationPolicies.Operations"/>.
/// </summary>
internal sealed class OperationsRequirement : AuthorizationHandler<OperationsRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationsRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.HasClaim(KeycloakClaims.AuthorizedParty, KeycloakClients.Operations)
            && HasRealmRole(context.User, KeycloakClaims.OperationsRole)
            && context.User.HasClaim(KeycloakClaims.AuthenticationMethods, KeycloakClaims.OneTimePassword))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool HasRealmRole(ClaimsPrincipal user, string role)
    {
        var realmAccess = user.FindFirst(KeycloakClaims.RealmAccess)?.Value;
        if (string.IsNullOrEmpty(realmAccess))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.ValueEquals(role));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
