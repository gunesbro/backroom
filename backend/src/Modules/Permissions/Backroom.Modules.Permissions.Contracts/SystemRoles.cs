namespace Backroom.Modules.Permissions.Contracts;

/// <summary>
/// The system roles and their permissions (docs/contracts/phase-1.md and phase-2.md, "Permissions used in Phase 1/2").
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

    // Phase 2 (docs/contracts/phase-2.md, "Permissions used in Phase 2"); existing organizations get them in the
    // migration AddFilePermissions.
    public const string FilesUpload = "files.upload";
    public const string FilesRemoveOthersPublic = "files.remove-others-public";

    /// <summary>Every permission key there is.</summary>
    public static IReadOnlyList<string> AllPermissions { get; } =
        [ChannelsCreatePublic, MembersInviteByEmail, MembersChooseRoleOnInvite, MembersManageInvitations, FilesUpload, FilesRemoveOthersPublic];

    /// <summary>Each system role and its permissions.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Permissions { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Owner] = AllPermissions,
        [Admin] = [ChannelsCreatePublic, MembersInviteByEmail, MembersChooseRoleOnInvite, MembersManageInvitations, FilesUpload, FilesRemoveOthersPublic],
        [Member] = [ChannelsCreatePublic, MembersInviteByEmail, FilesUpload],
    };
}
