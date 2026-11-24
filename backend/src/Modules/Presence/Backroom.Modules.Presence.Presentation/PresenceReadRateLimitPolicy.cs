using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;

namespace Backroom.Modules.Presence.Presentation;

/// <summary>
/// At most <see cref="PermitsPerMinute"/> presence reads per account and minute: a client reads when it shows people and when its
/// window regains focus, and live changes arrive through the hub, so more means a client gone wrong or one probing who is online.
/// Counts are in memory per instance (a distributed limiter comes with the deploy phase).
/// </summary>
internal sealed class PresenceReadRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const string Name = "presence-read";

    public const int PermitsPerMinute = 120;

    private static readonly Error s_limited =
        Error.RateLimited("presence.rate_limited", "Presence was read too often. Try again in a minute.", TimeSpan.FromMinutes(1));

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = static async (context, _) =>
        await s_limited.ToProblem().ExecuteAsync(context.HttpContext);

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var account = httpContext.User.FindFirst(KeycloakClaims.Subject)?.Value;
        if (string.IsNullOrEmpty(account))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        return RateLimitPartition.GetSlidingWindowLimiter(
            account,
            static _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            });
    }
}
