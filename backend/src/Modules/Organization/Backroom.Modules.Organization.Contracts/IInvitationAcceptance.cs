using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// The invited person joins the organization (docs/contracts/phase-1.md, flow 3, step 6). Called by
/// ControlPlaneConnector for <c>InvitationAccepted</c>, in the unit of work that records the message, which is
/// already in the invitation's organization; the membership, the role and <see cref="MemberAdded"/> are written in
/// that unit of work.
/// </summary>
public interface IInvitationAcceptance
{
    /// <summary>
    /// Adds the account as an active member with the invited role, or only marks the invitation accepted if the
    /// account is an active member already. Failures:
    /// <list type="bullet">
    /// <item><see cref="ErrorKind.NotFound"/> (<c>organization.invitation_not_found</c>): this cell does not know the
    /// organization or the invitation (yet). A missing prerequisite, not a decision: the caller retries later.</item>
    /// <item>Business rejections, final: <c>organization.invitation_acceptance_invalid</c> (values the columns cannot
    /// hold), <c>organization.invitation_not_pending</c>, <c>organization.invitation_expired</c>,
    /// <c>organization.invitation_email_mismatch</c>, <c>organization.inviter_not_authorized</c>,
    /// <c>organization.member_deactivated</c>.</item>
    /// </list>
    /// </summary>
    Task<Result> AcceptAsync(AcceptedInvitation invitation, CancellationToken cancellationToken);
}
