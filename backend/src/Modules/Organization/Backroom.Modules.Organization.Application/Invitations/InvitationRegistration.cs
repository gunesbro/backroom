using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Marks a code version registered with the control plane and publishes <see cref="InvitationReady"/> once for it
/// (docs/contracts/phase-1.md, flow 3, step 3). Under the organization lock, so a resend or revoke either committed
/// before (the conditions no longer hold) or waits for this one.
/// </summary>
internal sealed partial class InvitationRegistration(
    IInvitationStore invitations,
    IPermissionChecker permissions,
    IOrganizationMembershipCheck membership,
    IOutbox outbox,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<InvitationRegistration> logger) : IInvitationRegistration
{
    public async Task<bool> MarkRegisteredAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken)
    {
        var organizationId = context.OrganizationId
            ?? throw new InvalidOperationException("The unit of work has not entered an organization.");

        // The same lock as every other invitation write, taken first, so the lock order is the same everywhere.
        var now = timeProvider.GetUtcNow();
        await invitations.LockOrganizationAsync(organizationId, cancellationToken);
        if (await invitations.GetAsync(invitationId, cancellationToken) is not { Status: "pending" } invitation || invitation.CodeVersion != codeVersion)
        {
            return false;
        }

        // Acceptance would refuse it, so the email would carry a link that cannot work.
        if (!await InviterAuthorization.MayStillInviteAsync(membership, permissions, invitation.CreatedBy, invitation.Role, cancellationToken))
        {
            LogInviterNotAuthorized(logger, invitationId, organizationId);
            return false;
        }

        if (!await invitations.MarkRegisteredAsync(invitationId, codeVersion, now, cancellationToken))
        {
            return false;
        }

        await outbox.AddAsync(
            new InvitationReady(Guid.CreateVersion7(now), organizationId, now, invitationId, codeVersion, InvitationReady.DefaultLocale),
            cancellationToken);
        LogRegistered(logger, invitationId, codeVersion, organizationId);
        return true;
    }

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Invitation {InvitationId} code version {CodeVersion} registered with the control plane in organization {OrganizationId}.")]
    private static partial void LogRegistered(ILogger logger, Guid invitationId, int codeVersion, Guid organizationId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Invitation {InvitationId} in organization {OrganizationId} is not sent: its inviter can no longer invite into its role.")]
    private static partial void LogInviterNotAuthorized(ILogger logger, Guid invitationId, Guid organizationId);
}
