namespace Backroom.Modules.Permissions.Contracts;

/// <summary>
/// Sets up an organization's roles (Permissions CLAUDE.md). Called by Organization inside its unit of work,
/// which is already in that organization; writes only Permissions' tables. Both calls are idempotent.
/// </summary>
public interface IPermissionsProvisioning
{
    /// <summary>Creates the system roles Owner, Admin and Member with their permissions.</summary>
    Task InitializeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Gives <paramref name="accountId"/> the system role <paramref name="roleKey"/> (see <see cref="SystemRoles"/>).
    /// It does not check who may grant what: only organization provisioning may assign Owner, and other
    /// callers (invitation acceptance) must check that the granting member may give the role first.
    /// </summary>
    Task AssignRoleAsync(Guid organizationId, Guid accountId, string roleKey, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every role of <paramref name="accountId"/> in the organization, so the account holds no permission
    /// there. Called when a member is removed, and before a removed member who rejoins gets the invited role, so no
    /// earlier role comes back.
    /// </summary>
    Task RemoveMemberAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken);
}
