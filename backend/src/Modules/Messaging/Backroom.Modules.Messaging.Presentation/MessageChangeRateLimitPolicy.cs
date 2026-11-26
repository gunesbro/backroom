using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// At most <see cref="PermitsPerMinute"/> edits and, separately, as many deletes per account and organization and minute
/// (docs/contracts/phase-2.md, flow 2, "Rate limits"; <c>429 messaging.rate_limited</c> with <c>Retry-After</c>). Refused
/// requests count too. The counts are in memory per instance (a distributed limiter comes with the deploy phase); the partition of an
/// account that stops is released when its window has passed.
/// </summary>
internal abstract class MessageChangeRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const int PermitsPerMinute = 30;

    private static readonly Error s_limited =
        Error.RateLimited("messaging.rate_limited", "Too many changes to messages; try again in a minute.", TimeSpan.FromMinutes(1));

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = static async (context, _) =>
        await s_limited.ToProblem().ExecuteAsync(context.HttpContext);

    protected abstract string Kind { get; }

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
            $"{Kind}:{organizationId:N}:{account}",
            static _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            });
    }
}

/// <summary>The limit of <c>PATCH .../messages/{id}</c>.</summary>
internal sealed class MessageEditRateLimitPolicy : MessageChangeRateLimitPolicy
{
    public const string Name = "messaging-message-edit";

    protected override string Kind => "edit";
}

/// <summary>The limit of <c>DELETE .../messages/{id}</c>.</summary>
internal sealed class MessageDeleteRateLimitPolicy : MessageChangeRateLimitPolicy
{
    public const string Name = "messaging-message-delete";

    protected override string Kind => "delete";
}
