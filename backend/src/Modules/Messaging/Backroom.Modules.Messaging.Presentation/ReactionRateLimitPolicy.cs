using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// At most <see cref="PermitLimit"/> reactions (adding and removing together) per account and organization in <see cref="Window"/>
/// (docs/contracts/phase-2.md, flow 2, "Rate limits"; <c>429 messaging.rate_limited</c> with <c>Retry-After</c>). Refused requests
/// count too. The counts are in memory per instance (a distributed limiter comes with the deploy phase); the partition of an account
/// that stops is released when its window has passed.
/// </summary>
internal sealed class ReactionRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const string Name = "messaging-reaction";

    public const int PermitLimit = 30;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private static readonly Error s_limited =
        Error.RateLimited("messaging.rate_limited", "Too many reactions; try again in a few seconds.", Window);

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = static async (context, _) =>
        await s_limited.ToProblem().ExecuteAsync(context.HttpContext);

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Authorization has run: the account is signed in.
        var account = httpContext.User.FindFirst(KeycloakClaims.Subject)?.Value;
        // The organization as a GUID, so every spelling of it shares one bucket; an unparsable one is answered 404 by the endpoint.
        if (string.IsNullOrEmpty(account) || !Guid.TryParse(httpContext.Request.RouteValues["orgId"]?.ToString(), out var organizationId))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        return RateLimitPartition.GetSlidingWindowLimiter(
            $"reaction:{organizationId:N}:{account}",
            static _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = PermitLimit,
                Window = Window,
                SegmentsPerWindow = 5,
                QueueLimit = 0,
            });
    }
}
