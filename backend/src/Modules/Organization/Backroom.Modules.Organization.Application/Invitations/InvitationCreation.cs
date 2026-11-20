using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Domain.Email;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Creates an invitation (docs/contracts/phase-1.md, flow 3, steps 1 and 2). The endpoint has checked the
/// membership and <c>members.invite-by-email</c>. Everything is checked before the first write; the invitation,
/// the revocation of the one it replaces and their events are written in the request's unit of work.
/// </summary>
internal sealed partial class InvitationCreation(
    IInvitationStore invitations,
    IPermissionChecker permissions,
    IInvitationCodeIssuer codes,
    InvitationSendLimits limits,
    IOutbox outbox,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<InvitationCreation> logger)
{
    public static readonly Error EmailInvalid =
        Error.Validation("organization.invitation_email_invalid", "The email must be exactly one valid address.");

    public static readonly Error RoleInvalid =
        Error.Validation("organization.invitation_role_invalid", "The role must be admin or member.");

    public static readonly Error Forbidden =
        Error.Forbidden("permissions.forbidden", "You do not have permission to do this.");

    public static readonly Error AlreadyMember =
        Error.Conflict("organization.already_member", "This person is already a member.");

    public static readonly Error MemberDeactivated =
        Error.Conflict("organization.member_deactivated", "This person's membership is deactivated.");

    public static readonly Error PendingByAnother =
        Error.Conflict("organization.invitation_pending", "Someone else has already invited this person.");

    public async Task<Result<InvitationEntry>> CreateAsync(Guid inviterAccountId, string? email, string? role, CancellationToken cancellationToken)
    {
        var organizationId = context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");
        if (!EmailAddress.TryNormalize(email, out var invitedEmail))
        {
            return EmailInvalid;
        }

        if (await CheckRoleAsync(inviterAccountId, role, cancellationToken) is { } roleError)
        {
            return roleError;
        }

        var invitedRole = role ?? SystemRoles.Member;
        var now = timeProvider.GetUtcNow();
        await invitations.LockOrganizationAsync(organizationId, cancellationToken);

        switch (await invitations.FindMemberStatusAsync(invitedEmail, cancellationToken))
        {
            case "active":
                return AlreadyMember;
            case "deactivated":
                return MemberDeactivated;
        }

        // Replacing a pending invitation revokes it, which only its inviter or a manager may do (flow 3, step 8).
        if (await invitations.FindPendingInviterAsync(invitedEmail, cancellationToken) is { } pendingInviter
            && pendingInviter != inviterAccountId
            && !await permissions.HasPermissionAsync(inviterAccountId, SystemRoles.MembersManageInvitations, cancellationToken))
        {
            return PendingByAnother;
        }

        if (await limits.CheckAsync(inviterAccountId, invitedEmail, addsPending: true, now, cancellationToken) is { } limitError)
        {
            return limitError;
        }

        foreach (var replaced in await invitations.RevokePendingAsync(invitedEmail, inviterAccountId, now, cancellationToken))
        {
            await outbox.AddAsync(new InvitationRevoked(Guid.CreateVersion7(now), organizationId, now, replaced), cancellationToken);
            LogRevoked(logger, replaced, organizationId, inviterAccountId);
        }

        var id = Guid.CreateVersion7(now);
        var expiresAt = now + InvitationLimits.Lifetime;
        await invitations.AddAsync(
            new NewInvitation(id, organizationId, invitedEmail, invitedRole, codes.Issue(id), inviterAccountId, now, expiresAt),
            cancellationToken);
        await outbox.AddAsync(
            new InvitationCreated(Guid.CreateVersion7(now), organizationId, now, id, CodeVersion: 1, invitedEmail, invitedRole, expiresAt),
            cancellationToken);
        LogCreated(logger, id, organizationId, inviterAccountId);
        return new InvitationEntry(id, invitedEmail, invitedRole, inviterAccountId, now, expiresAt);
    }

    // Choosing a role needs its own permission; and nobody invites into a role they could not grant, the default included.
    private async Task<Error?> CheckRoleAsync(Guid inviterAccountId, string? role, CancellationToken cancellationToken)
    {
        if (role is not null)
        {
            if (!await permissions.HasPermissionAsync(inviterAccountId, SystemRoles.MembersChooseRoleOnInvite, cancellationToken))
            {
                return Forbidden;
            }

            if (role is not (SystemRoles.Admin or SystemRoles.Member))
            {
                return RoleInvalid;
            }
        }

        return await permissions.CanGrantRoleAsync(inviterAccountId, role ?? SystemRoles.Member, cancellationToken) ? null : Forbidden;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} created in organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogCreated(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} revoked in organization {OrganizationId} by account {AccountId} (replaced by a new one).")]
    private static partial void LogRevoked(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId);
}
