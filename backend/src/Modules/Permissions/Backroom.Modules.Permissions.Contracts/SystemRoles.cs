namespace Backroom.Modules.Permissions.Contracts;

/// <summary>
/// The system roles and the Phase 1 permissions (docs/contracts/phase-1.md, "Permissions used in Phase 1").
/// Owner has every permission and cannot be edited; the checker treats Owner as having all, so a permission
/// added later needs no backfill for existing organizations' Owner roles.
/// </summary>
public static class SystemRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public const string ChannelsCreatePublic = "channels.create-public";
    public const string MembersInviteByEmail = "members.invite-by-email";
    public const string MembersChooseRoleOnInvite = "members.choose-role-on-invite";
    public const string MembersManageInvitations = "members.manage-invitations";

    /// <summary>Every permission key there is.</summary>
    public static IReadOnlyList<string> AllPermissions { get; } =
        [ChannelsCreatePublic, MembersInviteByEmail, MembersChooseRoleOnInvite, MembersManageInvitations];

    /// <summary>Each system role and its permissions.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Permissions { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Owner] = AllPermissions,
        [Admin] = [ChannelsCreatePublic, MembersInviteByEmail, MembersChooseRoleOnInvite, MembersManageInvitations],
        [Member] = [ChannelsCreatePublic, MembersInviteByEmail],
    };
}
