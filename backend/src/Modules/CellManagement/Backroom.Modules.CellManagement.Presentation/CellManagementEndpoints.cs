using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Presentation.RateLimiting;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.Modules.CellManagement.Application;
using Backroom.Modules.CellManagement.Application.Provisioning;
using Backroom.Modules.CellManagement.Application.Registration;

namespace Backroom.Modules.CellManagement.Presentation;

public static class CellManagementEndpoints
{
    public const string RegistrationRateLimit = "cell-registration";

    public const string SharedCellsRateLimit = "shared-cells";

    /// <summary>Request bodies are small; anything larger is refused before it is read.</summary>
    private const long MaxRequestBytes = 4 * 1024;

    /// <summary>Link messages are small; a membership snapshot of a large organization is the biggest.</summary>
    private const int MaxLinkMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The link service (gRPC) and the registration rate limit: attempts per client IP address; a cell
    /// registers once, so a few are plenty.
    /// </summary>
    public static IServiceCollection AddCellManagementEndpoints(this IServiceCollection services)
    {
        services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = MaxLinkMessageBytes;
            options.MaxSendMessageSize = MaxLinkMessageBytes;
            options.EnableDetailedErrors = false;
        });
        services.TryAddSingleton<CellLinkSessions>();
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RegistrationRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientNetwork.PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));

            // Clients cache the list for 5 minutes; a sign-in reads it once.
            options.AddPolicy(SharedCellsRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientNetwork.PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static IEndpointRouteBuilder MapCellManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/cells/register", RegisterAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RegistrationRateLimit)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes));

        endpoints.MapGet("/api/v1/cells/shared", SharedCellsAsync)
            .AllowAnonymous()
            .RequireRateLimiting(SharedCellsRateLimit);

        endpoints.MapPost("/api/v1/ops/cells", ProvisionAsync)
            .RequireAuthorization(AuthorizationPolicies.Operations)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes));

        endpoints.MapGrpcService<CellLinkEndpoint>().RequireAuthorization(AuthorizationPolicies.Cell);

        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(RegisterCellRequest request, CellRegistrationService registration, CancellationToken cancellationToken)
    {
        var result = await registration.RegisterAsync(request.RegistrationKey, request.PublicKey, request.Version, cancellationToken);
        return result.ToHttpResult(value => TypedResults.Ok(new RegisterCellResponse(value.CellId, value.ClientId, value.Issuer)));
    }

    private static async Task<IResult> SharedCellsAsync(
        ICellRegistry registry, SharedCellOptions options, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var ids = await registry.SharedCellIdsAsync(options.Max, cancellationToken);
        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Ok(new SharedCellsResponse([.. ids.Select(id => new SharedCell(id))]));
    }

    private static async Task<IResult> ProvisionAsync(
        ProvisionCellRequest request, CellProvisioningService provisioning, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // The response carries the registration key.
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!Uri.TryCreate(request.PublicUrl, UriKind.Absolute, out var publicUrl))
        {
            return CellManagementErrors.CellInvalid.ToProblem();
        }

        var result = await provisioning.ProvisionAsync(new NewCell(request.Id, request.Name, request.Type, request.Region, publicUrl), cancellationToken);
        return result.ToHttpResult(value => TypedResults.Created(
            new Uri($"/api/v1/ops/cells/{value.Id}", UriKind.Relative),
            new ProvisionCellResponse(value.Id, value.RegistrationKey, value.ExpiresAt)));
    }
}
