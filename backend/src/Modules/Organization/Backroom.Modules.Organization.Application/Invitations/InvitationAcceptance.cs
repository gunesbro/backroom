using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Email;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Organization.Application.Members;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// The invited person joins (docs/contracts/phase-1.md, flow 3, step 6), in the connector's unit of work, which is in
/// the invitation's organization. The control plane checked the code, the expiry and the account's verified email;
/// the cell checks again what it is the source of truth for: the invitation's state, its email, the inviter's rights
/// and the membership. The profile values cross the link, so they are bounded here again.
/// </summary>
/// <remarks>
/// The invitation is read once before the organization lock, so a cell that does not know the organization or the
/// invitation answers without locking (the lock would fail); every decision is made on the row read again under the
/// lock, as for revoke and resend.
/// </remarks>
internal sealed partial class InvitationAcceptance(
    IInvitationStore invitations,
    IMemberStore members,
    IPermissionChecker permissions,
    IPermissionsProvisioning roles,
    IOrganizationMembershipCheck membership,
    IOutbox outbox,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<InvitationAcceptance> logger) : IInvitationAcceptance
{
    /// <summary>The control plane's clock may be ahead of the expiry by this much (flow 3, step 6).</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(60);

    public static readonly Error NotFound =
        Error.NotFound("organization.invitation_not_found", "The invitation was not found.");

    public static readonly Error Invalid =
        Error.Validation("organization.invitation_acceptance_invalid", "The accepted invitation or the account's profile is not valid.");

    public static readonly Error EmailMismatch =
        Error.Conflict("organization.invitation_email_mismatch", "The invitation was sent to another email address.");

    public async Task<Result> AcceptAsync(AcceptedInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var organizationId = context.OrganizationId
            ?? throw new InvalidOperationException("The unit of work has not entered an organization.");
        // An acceptance from the future would stretch the expiry check; the clocks may differ by the tolerance.
        if (!IsValid(invitation) || invitation.AcceptedAt > timeProvider.GetUtcNow() + ClockTolerance)
        {
            return Reject(invitation, organizationId, Invalid);
        }

        // Row-level security hides invitations of other organizations, and an organization this cell does not have.
        if (await invitations.GetAsync(invitation.InvitationId, cancellationToken) is null)
        {
            LogNotFound(logger, invitation.InvitationId, organizationId);
            return NotFound;
        }

        // The same lock as every other invitation write, taken first, so the lock order is the same everywhere.
        await invitations.LockOrganizationAsync(organizationId, cancellationToken);
        if (await invitations.GetAsync(invitation.InvitationId, cancellationToken) is not { Status: "pending", Email: { } invitedEmail } pending)
        {
            return Reject(invitation, organizationId, InvitationManagement.NotPending);
        }

        // The control plane's time of acceptance decides, not the delivery time.
        if (invitation.AcceptedAt > pending.ExpiresAt + ClockTolerance)
        {
            return Reject(invitation, organizationId, InvitationManagement.Expired);
        }

        if (!EmailAddress.TryNormalize(invitation.Email, out var email) || !string.Equals(email, invitedEmail, StringComparison.Ordinal))
        {
            return Reject(invitation, organizationId, EmailMismatch);
        }

        switch (await members.GetStatusAsync(invitation.AccountId, cancellationToken))
        {
            case "active":
                // Already in: the role stays as it is, and the inviter's rights do not matter.
                await MarkAcceptedAsync(invitation, cancellationToken);
                LogAlreadyMember(logger, invitation.InvitationId, organizationId, invitation.AccountId);
                return Result.Success;
            case "deactivated":
                return Reject(invitation, organizationId, InvitationCreation.MemberDeactivated);
        }

        if (!await InviterAuthorization.MayStillInviteAsync(membership, permissions, pending.CreatedBy, pending.Role, cancellationToken))
        {
            return Reject(invitation, organizationId, InvitationManagement.InviterNotAuthorized);
        }

        await MarkAcceptedAsync(invitation, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var version = await members.JoinAsync(
            new JoiningMember(organizationId, invitation.AccountId, invitation.DisplayName, email, invitation.Locale, now),
            cancellationToken)
            ?? throw new InvalidOperationException("The membership changed while the organization was locked.");
        if (version > 1)
        {
            // A removed member rejoining: only the invited role, never one held before.
            await roles.RemoveMemberAsync(organizationId, invitation.AccountId, cancellationToken);
        }

        await roles.AssignRoleAsync(organizationId, invitation.AccountId, pending.Role, cancellationToken);
        await outbox.AddAsync(new MemberAdded(Guid.CreateVersion7(now), organizationId, now, invitation.AccountId, pending.Role, version), cancellationToken);
        LogAccepted(logger, invitation.InvitationId, organizationId, invitation.AccountId, version);
        return Result.Success;
    }

    // Under the lock nothing else changes the invitation, so it is still pending.
    private async Task MarkAcceptedAsync(AcceptedInvitation invitation, CancellationToken cancellationToken)
    {
        if (!await invitations.MarkAcceptedAsync(invitation.InvitationId, invitation.AccountId, invitation.AcceptedAt, cancellationToken))
        {
            throw new InvalidOperationException("The invitation changed while the organization was locked.");
        }
    }

    private Error Reject(AcceptedInvitation invitation, Guid organizationId, Error error)
    {
        LogRejected(logger, invitation.InvitationId, organizationId, invitation.AccountId, error.Code);
        return error;
    }

    // The column limits of organization.members; the control plane took the values from the account's token.
    private static bool IsValid(AcceptedInvitation invitation) =>
        invitation.InvitationId != Guid.Empty
        && invitation.AccountId != Guid.Empty
        && ColumnText.Fits(invitation.DisplayName, 1, 400)
        && ColumnText.Fits(invitation.Locale, 2, 35);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} accepted in organization {OrganizationId} by account {AccountId}, membership version {Version}.")]
    private static partial void LogAccepted(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId, long version);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} accepted in organization {OrganizationId} by account {AccountId}, who was already a member.")]
    private static partial void LogAlreadyMember(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Security: acceptance of invitation {InvitationId} in organization {OrganizationId} by account {AccountId} refused: {Code}.")]
    private static partial void LogRejected(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId, string code);

    [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "Acceptance of invitation {InvitationId} in organization {OrganizationId} waits: the cell does not know the invitation.")]
    private static partial void LogNotFound(ILogger logger, Guid invitationId, Guid organizationId);
}
