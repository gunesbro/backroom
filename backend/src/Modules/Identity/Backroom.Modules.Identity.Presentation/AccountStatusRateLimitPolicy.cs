using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.Modules.Identity.Application;

namespace Backroom.Modules.Identity.Presentation;

/// <summary>
/// At most <see cref="PermitsPerMinute"/> status changes per account and minute (docs/contracts/phase-2.md, "Control plane"), refused
/// ones included, so a client cannot flood the cells with <c>AccountStatusChanged</c>. Counts are kept in memory per instance; the
/// partition of an account that stops changing its status is released when its window has passed.
/// </summary>
internal sealed class AccountStatusRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const string Name = "identity-account-status";

    public const int PermitsPerMinute = 30;

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = static async (context, _) =>
        await IdentityErrors.RateLimited.ToProblem().ExecuteAsync(context.HttpContext);

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Authorization has run: the account is signed in.
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
