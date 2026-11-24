using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.CellManagement.Application.Listing;

namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>
/// Audits operations calls that authentication or authorization refuses (401 and 403), then lets the default
/// handler answer. Only endpoints marked with <see cref="OpsAction"/> are audited.
/// </summary>
internal sealed class OpsAuditAuthorizationHandler(OpsAudit audit) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (!authorizeResult.Succeeded && context.GetEndpoint()?.Metadata.GetMetadata<OpsAction>() is { } action)
        {
            Guid? accountId = Guid.TryParse(context.User.FindFirst(KeycloakClaims.Subject)?.Value, out var id) ? id : null;
            audit.Record(accountId, action.Name, authorizeResult.Forbidden ? OpsAudit.Forbidden : OpsAudit.Unauthenticated);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
