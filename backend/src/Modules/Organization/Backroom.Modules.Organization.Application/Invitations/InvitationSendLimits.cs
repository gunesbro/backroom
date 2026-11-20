using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// The daily limits on invitation emails (docs/contracts/phase-1.md, flow 3, step 1). A send is a new invitation
/// or a resend; both count towards the limits per sender, per organization and per recipient. Run after the
/// organization lock, so concurrent sends see each other.
/// </summary>
internal sealed class InvitationSendLimits(IInvitationStore invitations)
{
    public const string RateLimitedCode = "organization.invitation_rate_limited";
    private const string RateLimitedDescription = "Too many invitations. Try again later.";

    /// <summary>
    /// The 429 to answer, or <see langword="null"/>. With <paramref name="addsPending"/> the pending cap applies too,
    /// not counting a pending invitation to <paramref name="email"/>, which the new one replaces.
    /// </summary>
    public async Task<Error?> CheckAsync(Guid senderAccountId, string email, bool addsPending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recent = await invitations.CountRecentAsync(senderAccountId, email, now - InvitationLimits.Window, cancellationToken);

        // Retry-After is when every exceeded limit has cleared, so a client that waits is not refused again.
        TimeSpan? retryAfter = null;
        if (recent.ByInviter >= InvitationLimits.PerInviterPerDay)
        {
            retryAfter = Later(retryAfter, UntilOldestLeaves(recent.OldestByInviter, now));
        }

        if (recent.InOrganization >= InvitationLimits.PerOrganizationPerDay)
        {
            retryAfter = Later(retryAfter, UntilOldestLeaves(recent.OldestInOrganization, now));
        }

        if (recent.ToRecipient >= InvitationLimits.PerRecipientPerDay)
        {
            retryAfter = Later(retryAfter, UntilOldestLeaves(recent.OldestToRecipient, now));
        }

        if (addsPending && await invitations.CountPendingExceptAsync(email, cancellationToken) >= InvitationLimits.PendingPerOrganization)
        {
            retryAfter = Later(retryAfter, InvitationLimits.PendingRetryAfter);
        }

        return retryAfter is { } wait ? Error.RateLimited(RateLimitedCode, RateLimitedDescription, wait) : null;
    }

    // A count falls again when its oldest send leaves the window.
    private static TimeSpan UntilOldestLeaves(DateTimeOffset? oldest, DateTimeOffset now)
    {
        var wait = (oldest ?? now) + InvitationLimits.Window - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }

    private static TimeSpan Later(TimeSpan? current, TimeSpan candidate) =>
        current is { } value && value > candidate ? value : candidate;
}
