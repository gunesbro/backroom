namespace Backroom.Modules.Permissions.Contracts;

/// <summary>
/// What an account's roles allow in the organization of the current unit of work; row-level security limits every
/// read to it. Permissions are read once per scope, so a role change takes effect on the next request, not later in
/// the same scope.
/// <para>
/// This reads role assignments only and does not check the membership: a removed member's roles may still be there.
/// Endpoints under <c>InOrganization()</c> have passed that check; a caller outside it (such as invitation acceptance)
/// must check the membership itself.
/// </para>
/// </summary>
public interface IPermissionChecker
{
    /// <summary>Whether the account holds the permission, one of <see cref="SystemRoles.AllPermissions"/>. Another key is a programming error and throws.</summary>
    Task<bool> HasPermissionAsync(Guid accountId, string permission, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the account may give someone the role: it exists, is not Owner, and all its permissions are ones the
    /// account has.
    /// </summary>
    Task<bool> CanGrantRoleAsync(Guid accountId, string roleKey, CancellationToken cancellationToken);
}
