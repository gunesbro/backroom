using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Revoking and resending a pending invitation (docs/contracts/phase-1.md, flow 3, step 8). The endpoint has checked
/// the membership. Only the inviter or a member with <c>members.manage-invitations</c> may act on an invitation;
/// to anyone else it does not exist, so the answer does not reveal who was invited.
/// </summary>
/// <remarks>
/// Who may act is decided before the organization lock, so members without that right cannot hold up other
/// invitation writes; the lock is then taken and the invitation read again, because its state may have changed.
/// </remarks>
internal sealed partial class InvitationManagement(
    IInvitationStore invitations,
    IPermissionChecker permissions,
    IOrganizationMembershipCheck membership,
    IInvitationCodeIssuer codes,
    InvitationSendLimits limits,
    IOutbox outbox,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<InvitationManagement> logger)
{
    public static readonly Error NotFound =
        Error.NotFound("organization.invitation_not_found", "The invitation was not found.");

    public static readonly Error NotPending =
        Error.Conflict("organization.invitation_not_pending", "The invitation was already accepted, revoked or has expired.");

    public static readonly Error Expired =
        Error.Conflict("organization.invitation_expired", "The invitation has expired. Send a new one instead.");

    public static readonly Error InviterNotAuthorized =
        Error.Conflict("organization.inviter_not_authorized", "The person who sent this invitation can no longer invite. Send a new one instead.");

    /// <summary>Revokes a pending invitation; its link stops working.</summary>
    public async Task<Result> RevokeAsync(Guid accountId, Guid invitationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (await FindForAsync(accountId, invitationId, cancellationToken) is null)
        {
            return NotFound;
        }

        // The same lock as creation, taken before any invitation row is changed, so the lock order is the same everywhere.
        var now = timeProvider.GetUtcNow();
        await invitations.LockOrganizationAsync(organizationId, cancellationToken);
        if (!await invitations.RevokeAsync(invitationId, accountId, now, cancellationToken))
        {
            return NotPending;
        }

        await outbox.AddAsync(new InvitationRevoked(Guid.CreateVersion7(now), organizationId, now, invitationId), cancellationToken);
        LogRevoked(logger, invitationId, organizationId, accountId);
        return Result.Success;
    }

    /// <summary>
    /// Sends a pending invitation again with a new code (new salt, current key version, next code version), so the
    /// old link stops working; the expiry stays. Counts towards the daily limits like a new invitation.
    /// </summary>
    public async Task<Result<InvitationEntry>> ResendAsync(Guid accountId, Guid invitationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (await FindForAsync(accountId, invitationId, cancellationToken) is null)
        {
            return NotFound;
        }

        var now = timeProvider.GetUtcNow();
        await invitations.LockOrganizationAsync(organizationId, cancellationToken);
        if (await invitations.GetAsync(invitationId, cancellationToken) is not { Status: "pending", Email: { } email } invitation)
        {
            return NotPending;
        }

        if (invitation.ExpiresAt <= now)
        {
            return Expired;
        }

        if (await CheckSendersAsync(accountId, invitation, cancellationToken) is { } senderError)
        {
            return senderError;
        }

        if (await limits.CheckAsync(accountId, email, addsPending: false, now, cancellationToken) is { } limitError)
        {
            return limitError;
        }

        var codeVersion = invitation.CodeVersion + 1;
        if (!await invitations.ReissueAsync(invitationId, codes.Issue(invitationId), codeVersion, accountId, now, cancellationToken))
        {
            return NotPending;
        }

        await outbox.AddAsync(
            new InvitationCreated(Guid.CreateVersion7(now), organizationId, now, invitationId, codeVersion, email, invitation.Role, invitation.ExpiresAt),
            cancellationToken);
        LogResent(logger, invitationId, codeVersion, organizationId, accountId);
        return new InvitationEntry(invitationId, email, invitation.Role, invitation.CreatedBy, invitation.CreatedAt, invitation.ExpiresAt);
    }

    // Acceptance refuses an invitation whose inviter can no longer invite into its role (flow 3, step 6), so a resend
    // of one would only send a link that cannot work. Whoever resends must be able to grant the role too.
    private async Task<Error?> CheckSendersAsync(Guid accountId, InvitationState invitation, CancellationToken cancellationToken)
    {
        if (!await InviterAuthorization.MayStillInviteAsync(membership, permissions, invitation.CreatedBy, invitation.Role, cancellationToken))
        {
            return invitation.CreatedBy == accountId ? InvitationCreation.Forbidden : InviterNotAuthorized;
        }

        return invitation.CreatedBy == accountId || await permissions.CanGrantRoleAsync(accountId, invitation.Role, cancellationToken)
            ? null
            : InvitationCreation.Forbidden;
    }

    // The invitation if the account may act on it: its inviter, or a manager. The same queries run whether or not
    // the invitation exists, so the time taken does not tell either.
    private async Task<InvitationState?> FindForAsync(Guid accountId, Guid invitationId, CancellationToken cancellationToken)
    {
        var isManager = await permissions.HasPermissionAsync(accountId, SystemRoles.MembersManageInvitations, cancellationToken);
        var invitation = await invitations.GetAsync(invitationId, cancellationToken);
        return invitation is not null && (isManager || invitation.CreatedBy == accountId) ? invitation : null;
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} revoked in organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogRevoked(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} resent as code version {CodeVersion} in organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogResent(ILogger logger, Guid invitationId, int codeVersion, Guid organizationId, Guid accountId);
}
