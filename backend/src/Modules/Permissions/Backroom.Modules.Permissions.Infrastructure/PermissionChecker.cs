using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Permissions.Infrastructure;

/// <summary>Permission checks with hand-written SQL in the scope's unit of work (row-level security applies).</summary>
internal sealed class PermissionChecker(DatabaseSession session) : IPermissionChecker, IOrganizationPermissionCheck
{
    // LEFT JOIN: a role without permissions still shows that the account has it (Owner must count even with none left).
    private const string AccountRolesSql = """
        SELECT r.key, p.permission
        FROM permissions.member_roles m
        JOIN permissions.roles r ON r.org_id = m.org_id AND r.id = m.role_id
        LEFT JOIN permissions.role_permissions p ON p.org_id = r.org_id AND p.role_id = r.id
        WHERE m.account_id = $1
        """;

    private const string RolePermissionsSql = """
        SELECT p.permission
        FROM permissions.roles r
        LEFT JOIN permissions.role_permissions p ON p.org_id = r.org_id AND p.role_id = r.id
        WHERE r.key = $1
        """;

    private Guid _grantsAccount;
    private Grants? _grants;

    public async Task<bool> HasPermissionAsync(Guid accountId, string permission, CancellationToken cancellationToken)
    {
        if (!SystemRoles.AllPermissions.Contains(permission, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        }

        return (await GrantsAsync(accountId, cancellationToken)).Allows(permission);
    }

    public async Task<IReadOnlyList<string>> ListPermissionsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var grants = await GrantsAsync(accountId, cancellationToken);
        // Only keys the code knows: a row left behind by a removed permission is not handed out.
        return [.. SystemRoles.AllPermissions.Where(grants.Allows).Order(StringComparer.Ordinal)];
    }

    public async Task<bool> CanGrantRoleAsync(Guid accountId, string roleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleKey);
        if (roleKey == SystemRoles.Owner)
        {
            return false;
        }

        var grants = await GrantsAsync(accountId, cancellationToken);
        if (!grants.HasAnyRole)
        {
            return false;
        }

        await using var command = new NpgsqlCommand(RolePermissionsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = roleKey });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roleExists = false;
        var allHeld = true;
        while (await reader.ReadAsync(cancellationToken))
        {
            roleExists = true;
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                allHeld &= grants.Allows(reader.GetString(0));
            }
        }

        return roleExists && allHeld;
    }

    private async Task<Grants> GrantsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (_grants is not null && _grantsAccount == accountId)
        {
            return _grants;
        }

        await using var command = new NpgsqlCommand(AccountRolesSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hasAnyRole = false;
        var isOwner = false;
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            hasAnyRole = true;
            isOwner |= reader.GetString(0) == SystemRoles.Owner;
            if (!await reader.IsDBNullAsync(1, cancellationToken))
            {
                permissions.Add(reader.GetString(1));
            }
        }

        _grantsAccount = accountId;
        _grants = new Grants(hasAnyRole, isOwner, permissions);
        return _grants;
    }

    private sealed record Grants(bool HasAnyRole, bool IsOwner, HashSet<string> Permissions)
    {
        // Owner has every permission, also one added after its roles were created.
        public bool Allows(string permission) => IsOwner || Permissions.Contains(permission);
    }
}
