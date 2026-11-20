using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Organization.Application.Invitations;
using Backroom.Modules.Organization.Application.Queries;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Presentation;

public static class OrganizationEndpoints
{
    /// <summary>The feature flag that turns on the invitation endpoints.</summary>
    public const string InvitationsFlag = "Features:Invitations";

    /// <summary>An email and a role; anything larger is refused before it is read.</summary>
    private const long MaxInvitationRequestBytes = 4 * 1024;

    /// <summary>The limit on invitation attempts; the host adds <c>UseRateLimiter()</c> after authorization.</summary>
    public static IServiceCollection AddOrganizationEndpoints(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(InvitationAttemptRateLimitPolicy.Name, new InvitationAttemptRateLimitPolicy()));

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/orgs/{orgId:guid}", GetAsync).InOrganization();
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/members", ListMembersAsync).InOrganization();

        // Off until an invitation can be registered, sent and accepted (CLAUDE.md: unfinished features ship
        // behind a flag): until then a created invitation would never reach its recipient.
        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(InvitationsFlag))
        {
            endpoints.MapPost("/api/v1/orgs/{orgId:guid}/invitations", CreateInvitationAsync)
                .InOrganization()
                .RequirePermission(SystemRoles.MembersInviteByEmail)
                .RequireRateLimiting(InvitationAttemptRateLimitPolicy.Name)
                .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxInvitationRequestBytes));
            // Members who may invite see their own; the query decides who sees all.
            endpoints.MapGet("/api/v1/orgs/{orgId:guid}/invitations", ListInvitationsAsync).InOrganization();
            // The inviter or a manager; the service decides, and answers 404 to anyone else.
            endpoints.MapDelete("/api/v1/orgs/{orgId:guid}/invitations/{invitationId:guid}", RevokeInvitationAsync).InOrganization();
            endpoints.MapPost("/api/v1/orgs/{orgId:guid}/invitations/{invitationId:guid}/resend", ResendInvitationAsync)
                .InOrganization()
                .RequireRateLimiting(InvitationAttemptRateLimitPolicy.Name);
        }

        return endpoints;
    }

    private static async Task<IResult> GetAsync(OrganizationQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.GetAsync(cancellationToken);
        return result.ToHttpResult(organization => TypedResults.Ok(new OrganizationResponse(organization.Id, organization.Name)));
    }

    private static async Task<IResult> ListMembersAsync(Guid? after, int? limit, ICurrentAccount account, OrganizationQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ListMembersAsync(account.AccountId, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new MembersResponse(
            [.. page.Members.Select(member => new MemberResponse(member.AccountId, member.DisplayName, member.Email))],
            page.Next)));
    }

    private static async Task<IResult> CreateInvitationAsync(CreateInvitationRequest request, ICurrentAccount account, InvitationCreation creation, CancellationToken cancellationToken)
    {
        var result = await creation.CreateAsync(account.AccountId, request.Email, request.Role, cancellationToken);
        return result.ToHttpResult(invitation => TypedResults.Created((string?)null, ToResponse(invitation)));
    }

    private static async Task<IResult> ListInvitationsAsync(Guid? after, int? limit, ICurrentAccount account, InvitationQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ListPendingAsync(account.AccountId, after, limit, cancellationToken);
        return result.ToHttpResult(page => TypedResults.Ok(new InvitationsResponse([.. page.Invitations.Select(ToResponse)], page.Next)));
    }

    private static async Task<IResult> RevokeInvitationAsync(Guid invitationId, ICurrentAccount account, InvitationManagement management, CancellationToken cancellationToken) =>
        (await management.RevokeAsync(account.AccountId, invitationId, cancellationToken)).ToHttpResult();

    private static async Task<IResult> ResendInvitationAsync(Guid invitationId, ICurrentAccount account, InvitationManagement management, CancellationToken cancellationToken)
    {
        var result = await management.ResendAsync(account.AccountId, invitationId, cancellationToken);
        return result.ToHttpResult(invitation => TypedResults.Ok(ToResponse(invitation)));
    }

    private static InvitationResponse ToResponse(InvitationEntry invitation) =>
        new(invitation.Id, invitation.Email, invitation.Role, invitation.CreatedBy, invitation.CreatedAt, invitation.ExpiresAt);
}
