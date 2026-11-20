using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Persistence;

namespace Backroom.BuildingBlocks.Presentation.Organizations;

public static class OrganizationEndpointExtensions
{
    private const string OrganizationRouteValue = "orgId";

    /// <summary>Set once the membership check has passed for this request.</summary>
    private const string MembershipCheckedItem = "Backroom.OrganizationMembershipChecked";

    /// <summary>
    /// The same answer for an unknown organization, one the account never belonged to, and an ended membership, so
    /// it does not reveal whether an organization exists.
    /// </summary>
    private static readonly Error s_notFound = Error.NotFound("organization.not_found", "The organization was not found.");

    private static readonly Error s_forbidden = Error.Forbidden("permissions.forbidden", "You do not have permission to do this.");

    /// <summary>
    /// Runs an endpoint under <c>{orgId}</c> for an active member only: requires a signed-in account, enters the route's
    /// organization, begins the unit of work and checks the membership before the handler runs. The route's value alone
    /// grants nothing: everything the transaction reads is limited to that organization by row-level security, and a
    /// failed check rolls it back.
    /// </summary>
    public static TBuilder InOrganization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.RequireAuthorization();
        builder.AddEndpointFilter(static (context, next) =>
        {
            var httpContext = context.HttpContext;
            if (!TryGetOrganizationId(httpContext, out var organizationId))
            {
                return ValueTask.FromResult<object?>(s_notFound.ToProblem());
            }

            httpContext.RequestServices.GetRequiredService<OrganizationContext>().Enter(organizationId);
            return next(context);
        });
        builder.WithUnitOfWork();
        builder.AddEndpointFilter(static async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var accountId = services.GetRequiredService<ICurrentAccount>().AccountId;
            var isMember = await services.GetRequiredService<IOrganizationMembershipCheck>()
                .IsActiveMemberAsync(accountId, context.HttpContext.RequestAborted);
            if (!isMember)
            {
                return s_notFound.ToProblem();
            }

            context.HttpContext.Items[MembershipCheckedItem] = true;
            return await next(context);
        });
        return builder;
    }

    /// <summary>
    /// Also requires a permission. Add it after <see cref="InOrganization"/>: outsiders then get the membership
    /// check's 404, and only members can learn that a permission is missing. Without that check having passed, the
    /// request fails with a server error: roles alone never let anyone in.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.Items.ContainsKey(MembershipCheckedItem))
            {
                throw new InvalidOperationException("RequirePermission must follow InOrganization on the endpoint.");
            }

            var services = context.HttpContext.RequestServices;
            var accountId = services.GetRequiredService<ICurrentAccount>().AccountId;
            var allowed = await services.GetRequiredService<IOrganizationPermissionCheck>()
                .HasPermissionAsync(accountId, permission, context.HttpContext.RequestAborted);
            return allowed ? await next(context) : s_forbidden.ToProblem();
        });
        return builder;
    }

    private static bool TryGetOrganizationId(HttpContext httpContext, out Guid organizationId)
    {
        var value = Convert.ToString(httpContext.Request.RouteValues[OrganizationRouteValue], CultureInfo.InvariantCulture);
        return Guid.TryParse(value, out organizationId) && organizationId != Guid.Empty;
    }
}
