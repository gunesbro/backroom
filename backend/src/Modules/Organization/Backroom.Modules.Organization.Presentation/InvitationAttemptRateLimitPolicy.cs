using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Errors;

namespace Backroom.Modules.Organization.Presentation;

/// <summary>
/// Limits invitation attempts per account and organization, refused ones included. The daily limits count only
/// created invitations, so without this a member could send address after address and learn from
/// <c>organization.already_member</c> who belongs to the organization. The limiter keeps its counts in memory per
/// instance; partitions of accounts that stop sending are released when their window has passed.
/// </summary>
internal sealed class InvitationAttemptRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const string Name = "organization-invitation-attempts";

    /// <summary>Twice the created invitations an inviter may send in a day, for typing mistakes.</summary>
    public const int PermitsPerDay = 100;

    private static readonly TimeSpan s_window = TimeSpan.FromDays(1);

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = static async (context, _) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromHours(1);
        var error = Error.RateLimited("organization.invitation_rate_limited", "Too many invitations. Try again later.", retryAfter);
        await error.ToProblem().ExecuteAsync(context.HttpContext);
    };

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Authorization has run: the account is signed in. The route's organization is not checked yet, so an
        // outsider spends their own permits.
        var account = httpContext.User.FindFirst(KeycloakClaims.Subject)?.Value;
        // The organization as a GUID, so every spelling of it shares one bucket; an unparsable one is answered 404 by the endpoint.
        var organizationParsed = Guid.TryParse(Convert.ToString(httpContext.Request.RouteValues["orgId"], CultureInfo.InvariantCulture), out var organization);
        if (string.IsNullOrEmpty(account) || !organizationParsed)
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        // A sliding window, so a full day's permits cannot be spent twice around the end of a fixed window.
        return RateLimitPartition.GetSlidingWindowLimiter(
            string.Concat(account, "/", organization.ToString("N")),
            static _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerDay,
                Window = s_window,
                SegmentsPerWindow = 24,
                QueueLimit = 0,
            });
    }
}
