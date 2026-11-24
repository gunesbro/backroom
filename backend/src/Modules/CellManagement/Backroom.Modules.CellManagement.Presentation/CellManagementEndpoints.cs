using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Presentation.RateLimiting;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.Modules.CellManagement.Application;
using Backroom.Modules.CellManagement.Application.Epoch;
using Backroom.Modules.CellManagement.Application.Listing;
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
        // Audits refused operations calls (401 and 403), then answers as usual.
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, OpsAuditAuthorizationHandler>();
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

        endpoints.MapGet("/api/v1/ops/cells", ListAsync)
            .RequireAuthorization(AuthorizationPolicies.Operations)
            .WithMetadata(new OpsAction(ListAction));

        endpoints.MapPost("/api/v1/ops/cells", ProvisionAsync)
            .RequireAuthorization(AuthorizationPolicies.Operations)
            .WithMetadata(new OpsAction(ProvisionAction))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes));

        // The restore and failover procedure: increment before the cell accepts writes (docs/runbooks/cell-restore-and-failover.md).
        endpoints.MapPost("/api/v1/ops/cells/{id}/epoch", IncrementEpochAsync)
            .RequireAuthorization(AuthorizationPolicies.Operations)
            .WithMetadata(new OpsAction(IncrementEpochAction))
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

    private const string ListAction = "ops.cells.list";

    private const string ProvisionAction = "ops.cells.provision";

    private const string IncrementEpochAction = "ops.cells.epoch.increment";

    private static async Task<IResult> ListAsync(
        CellListService cells, ICurrentAccount account, OpsAudit audit, HttpContext httpContext, string? after, int? limit, CancellationToken cancellationToken)
    {
        // Operations data is never kept by a browser or a proxy.
        httpContext.Response.Headers.CacheControl = "no-store";
        var accountId = account.AccountId;
        if (after is { Length: > 64 })
        {
            audit.Record(accountId, ListAction, OpsAudit.Invalid);
            return CellManagementErrors.CellInvalid.ToProblem();
        }

        try
        {
            var page = await cells.ListAsync(after, limit ?? CellListService.MaxPageSize, cancellationToken);
            audit.Record(accountId, ListAction, OpsAudit.Success);
            return TypedResults.Ok(new CellListResponse(
                [.. page.Cells.Select(cell => new CellSummaryResponse(
                    cell.Id, cell.Type, cell.Region, cell.Status, cell.Version, cell.LinkStatus, cell.LastHeartbeatAt,
                    cell.OrganizationCount, cell.PendingMessages, cell.RejectedMessages))],
                page.Next));
        }
        catch (OperationCanceledException)
        {
            audit.Record(accountId, ListAction, OpsAudit.Cancelled);
            throw;
        }
        catch (Exception)
        {
            audit.Record(accountId, ListAction, OpsAudit.Failed);
            throw;
        }
    }

    private static async Task<IResult> IncrementEpochAsync(
        string id, CellEpochService epochs, ICurrentAccount account, OpsAudit audit, HttpContext httpContext, CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var accountId = account.AccountId;
        // The cell id is not personal data; it is audited only once it passed the id rule.
        var target = CellIds.IsValid(id) ? id : null;
        try
        {
            var result = await epochs.IncrementAsync(id, cancellationToken);
            audit.Record(
                accountId,
                IncrementEpochAction,
                result.IsSuccess ? OpsAudit.Success : result.Error.Kind == ErrorKind.Validation ? OpsAudit.Invalid : OpsAudit.Rejected,
                target);
            return result.ToHttpResult(epoch => TypedResults.Ok(new CellEpochResponse(id, epoch.ToString(CultureInfo.InvariantCulture))));
        }
        catch (OperationCanceledException)
        {
            audit.Record(accountId, IncrementEpochAction, OpsAudit.Cancelled, target);
            throw;
        }
        catch (Exception)
        {
            audit.Record(accountId, IncrementEpochAction, OpsAudit.Failed, target);
            throw;
        }
    }

    private static async Task<IResult> ProvisionAsync(
        ProvisionCellRequest request,
        CellProvisioningService provisioning,
        ICurrentAccount account,
        OpsAudit audit,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // The response carries the registration key.
        httpContext.Response.Headers.CacheControl = "no-store";
        var accountId = account.AccountId;
        if (!Uri.TryCreate(request.PublicUrl, UriKind.Absolute, out var publicUrl))
        {
            audit.Record(accountId, ProvisionAction, OpsAudit.Invalid);
            return CellManagementErrors.CellInvalid.ToProblem();
        }

        try
        {
            var result = await provisioning.ProvisionAsync(new NewCell(request.Id, request.Name, request.Type, request.Region, publicUrl), cancellationToken);
            audit.Record(
                accountId,
                ProvisionAction,
                result.IsSuccess ? OpsAudit.Success : result.Error.Kind == ErrorKind.Validation ? OpsAudit.Invalid : OpsAudit.Rejected);
            return result.ToHttpResult(value => TypedResults.Created(
                new Uri($"/api/v1/ops/cells/{value.Id}", UriKind.Relative),
                new ProvisionCellResponse(value.Id, value.RegistrationKey, value.ExpiresAt)));
        }
        catch (OperationCanceledException)
        {
            audit.Record(accountId, ProvisionAction, OpsAudit.Cancelled);
            throw;
        }
        catch (Exception)
        {
            audit.Record(accountId, ProvisionAction, OpsAudit.Failed);
            throw;
        }
    }
}
