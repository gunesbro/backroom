using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Presentation.RateLimiting;
using Backroom.Modules.Identity.Application;

namespace Backroom.Modules.Identity.Presentation;

public static class IdentityEndpoints
{
    /// <summary>
    /// Keycloak's back-channel logout URL (docs/contracts/phase-1.md, flow 5). Only Keycloak calls it, from inside the deployment: the
    /// proxy in front of the control plane must not publish <c>/internal</c>. The call carries no bearer token (the logout token
    /// authenticates it).
    /// </summary>
    public const string BackchannelLogoutPath = "/internal/keycloak/backchannel-logout";

    private const string RateLimit = "backchannel-logout";

    /// <summary>A form with one token of at most 8 KB.</summary>
    private const long MaxRequestBytes = 16 * 1024;

    public static IServiceCollection AddIdentityEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LogoutTokenValidator>();
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Keycloak is one caller (one network); the limit only bounds what a stranger who reaches the URL can make us parse.
            options.AddPolicy(RateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientNetwork.PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 3000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(BackchannelLogoutPath, BackchannelLogoutAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimit)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> BackchannelLogoutAsync(
        HttpContext httpContext, LogoutTokenValidator validator, SessionRevocations revocations, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        // Required by the specification: the answer is never cached.
        httpContext.Response.Headers.CacheControl = "no-store";

        if (!httpContext.Request.HasFormContentType)
        {
            return Refuse(loggers, httpContext, "not a form");
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var token = form.TryGetValue("logout_token", out var values) && values.Count == 1 ? values[0] : null;
        var result = await validator.ValidateAsync(token, cancellationToken);
        if (!result.IsValid)
        {
            return Refuse(loggers, httpContext, result.Reason!);
        }

        _ = await revocations.RevokeAsync(result.AccountId, result.SessionId, cancellationToken);
        return TypedResults.Ok();
    }

    // 400 with the error body of the specification; the reason stays in the log, so a caller learns nothing about what was wrong.
    private static JsonHttpResult<LogoutError> Refuse(ILoggerFactory loggers, HttpContext httpContext, string reason)
    {
        IdentityLog.LogoutRefused(loggers.CreateLogger("Backroom.Modules.Identity.BackchannelLogout"), reason);
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return TypedResults.Json(
            new LogoutError("invalid_request", "The logout token is not valid."),
            statusCode: StatusCodes.Status400BadRequest);
    }

    internal sealed record LogoutError(
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("error_description")] string ErrorDescription);
}
