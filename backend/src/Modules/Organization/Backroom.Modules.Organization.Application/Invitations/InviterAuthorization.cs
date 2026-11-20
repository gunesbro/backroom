using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Whether an invitation's inviter could still send it: acceptance refuses one whose inviter is no longer an active
/// member who may invite into its role (docs/contracts/phase-1.md, flow 3, step 6), so its link would not work.
/// </summary>
internal static class InviterAuthorization
{
    public static async Task<bool> MayStillInviteAsync(
        IOrganizationMembershipCheck membership,
        IPermissionChecker permissions,
        Guid inviterAccountId,
        string role,
        CancellationToken cancellationToken) =>
        await membership.IsActiveMemberAsync(inviterAccountId, cancellationToken)
        && await permissions.HasPermissionAsync(inviterAccountId, SystemRoles.MembersInviteByEmail, cancellationToken)
        && await permissions.CanGrantRoleAsync(inviterAccountId, role, cancellationToken);
}
