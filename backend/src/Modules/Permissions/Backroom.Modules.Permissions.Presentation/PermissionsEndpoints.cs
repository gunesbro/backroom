using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Permissions.Presentation;

public static class PermissionsEndpoints
{
    public static IEndpointRouteBuilder MapPermissionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The caller's own permissions only: no route reads another account's. Clients use it to decide what to
        // offer; every endpoint still checks for itself.
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/me/permissions", ListMineAsync).InOrganization();
        return endpoints;
    }

    private static async Task<IResult> ListMineAsync(ICurrentAccount account, IPermissionChecker permissions, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // A role change takes effect on the next request: nothing between the client and the cell may keep the answer.
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new MyPermissionsResponse(await permissions.ListPermissionsAsync(account.AccountId, cancellationToken)));
    }
}
