using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.Modules.Directory.Application.Invitations;
using Backroom.Modules.Directory.Application.Organizations;

namespace Backroom.Modules.Directory.Presentation;

public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/me/organizations", ListMineAsync).RequireAuthorization();
        endpoints.MapGet("/api/v1/organizations/{id:guid}", GetStateAsync).RequireAuthorization();
        // Off until the cell can create organizations (step 3d-3b): until then the message would expire and
        // the organization fail after hours (CLAUDE.md: unfinished features ship behind a flag).
        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(OrganizationCreationFlag))
        {
            endpoints.MapPost("/api/v1/organizations", CreateAsync)
                .RequireAuthorization()
                .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes));
        }

        // Off until the whole invitation flow exists, like the cell's invitation endpoints.
        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(InvitationsFlag))
        {
            // Bearer tokens only (no cookies, so no CSRF); the code only in the body; one filter for the limits.
            var invitations = endpoints.MapGroup("/api/v1/invitations")
                .RequireAuthorization()
                .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxInvitationRequestBytes));
            var attempts = invitations.MapGroup(string.Empty).AddEndpointFilter<InvitationAttemptFilter>();
            attempts.MapPost("/resolve", ResolveAsync);
            attempts.MapPost("/accept", AcceptAsync);

            // Polled by the invite page and takes no code, so outside the attempt limits (a poll every few seconds
            // would use them up, and "none yet" is not a failed attempt).
            invitations.MapGet("/acceptance", GetAcceptanceAsync);
        }

        return endpoints;
    }

    /// <summary>The limits and filter of the invitation endpoints.</summary>
    public static IServiceCollection AddDirectoryEndpoints(this IServiceCollection services)
    {
        services.TryAddSingleton<InvitationAttemptLimits>();
        return services;
    }

    /// <summary>The feature flag that turns on organization creation.</summary>
    public const string OrganizationCreationFlag = "Features:OrganizationCreation";

    /// <summary>The feature flag that turns on resolving and accepting invitations (the cell's flag has the same name).</summary>
    public const string InvitationsFlag = "Features:Invitations";

    /// <summary>A code of 43 characters in JSON; anything larger is refused before it is read.</summary>
    private const long MaxInvitationRequestBytes = 1024;

    private static async Task<IResult> ResolveAsync(InvitationCodeRequest request, InvitationResolution resolution, CancellationToken cancellationToken)
    {
        var result = await resolution.ResolveAsync(request.Code, cancellationToken);
        return result.ToHttpResult(resolved => TypedResults.Ok(new ResolvedInvitationResponse(resolved.OrganizationName, resolved.ExpiresAt, resolved.MatchesAccount)));
    }

    private static async Task<IResult> AcceptAsync(InvitationCodeRequest request, InvitationAcceptance acceptance, CancellationToken cancellationToken)
    {
        var result = await acceptance.AcceptAsync(request.Code, cancellationToken);
        return result.ToHttpResult(accepted => TypedResults.Accepted(
            new Uri("/api/v1/invitations/acceptance", UriKind.Relative),
            new AcceptedInvitationResponse(accepted.OrganizationId, accepted.Status)));
    }

    private static async Task<IResult> GetAcceptanceAsync(HttpResponse response, InvitationAcceptance acceptance, CancellationToken cancellationToken)
    {
        // The state changes while the page polls it: never cached on the way.
        response.Headers.CacheControl = "no-store";
        var result = await acceptance.GetLatestAsync(cancellationToken);
        return result.ToHttpResult(latest => TypedResults.Ok(new InvitationAcceptanceResponse(latest.OrganizationId, latest.Status, latest.FailureCode)));
    }

    /// <summary>A name and a region; anything larger is refused before it is read.</summary>
    private const long MaxRequestBytes = 4 * 1024;

    private static async Task<IResult> CreateAsync(CreateOrganizationRequest request, OrganizationCreation creation, CancellationToken cancellationToken)
    {
        var result = await creation.CreateAsync(request.Name, request.Region, cancellationToken);
        return result.ToHttpResult(created => TypedResults.Accepted(
            new Uri($"/api/v1/organizations/{created.Id}", UriKind.Relative),
            new CreateOrganizationResponse(created.Id, created.Status, created.CellId, created.CellUrl)));
    }

    private static async Task<IResult> ListMineAsync(OrganizationQueries queries, CancellationToken cancellationToken)
    {
        var organizations = await queries.ListMineAsync(cancellationToken);
        return TypedResults.Ok(new MyOrganizationsResponse(
            [.. organizations.Select(organization => new AccountOrganizationResponse(organization.Id, organization.Name, organization.CellId, organization.CellUrl))]));
    }

    private static async Task<IResult> GetStateAsync(Guid id, OrganizationQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.GetStateAsync(id, cancellationToken);
        return result.ToHttpResult(state => TypedResults.Ok(new OrganizationStateResponse(state.Id, state.Status, state.FailureCode)));
    }
}
