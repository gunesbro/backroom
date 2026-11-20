using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Permissions.Infrastructure;

/// <summary>System roles and role assignments with hand-written SQL in the caller's unit of work (row-level security applies).</summary>
internal sealed class PermissionsProvisioning(DatabaseSession session, TimeProvider timeProvider) : IPermissionsProvisioning
{
    private const string AddRoleSql = """
        INSERT INTO permissions.roles (id, org_id, key, is_system) VALUES ($1, $2, $3, true)
        ON CONFLICT (org_id, key) DO NOTHING
        """;

    private const string AddPermissionsSql = """
        INSERT INTO permissions.role_permissions (org_id, role_id, permission)
        SELECT r.org_id, r.id, p FROM permissions.roles r, unnest($3::text[]) p
        WHERE r.org_id = $1 AND r.key = $2
        ON CONFLICT DO NOTHING
        """;

    private const string AssignSql = """
        INSERT INTO permissions.member_roles (org_id, account_id, role_id)
        SELECT $1, $2, id FROM permissions.roles WHERE org_id = $1 AND key = $3
        ON CONFLICT DO NOTHING
        """;

    // Uses the primary key's (org_id, account_id) prefix.
    private const string RemoveMemberSql = "DELETE FROM permissions.member_roles WHERE org_id = $1 AND account_id = $2";

    private const string RoleExistsSql = "SELECT EXISTS (SELECT 1 FROM permissions.roles WHERE org_id = $1 AND key = $2)";

    public async Task InitializeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        foreach (var (role, permissions) in SystemRoles.Permissions)
        {
            await using (var command = new NpgsqlCommand(AddRoleSql, session.Connection, session.Transaction))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7(timeProvider.GetUtcNow()) });
                command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = new NpgsqlCommand(AddPermissionsSql, session.Connection, session.Transaction))
            {
                command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
                command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
                command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. permissions] });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    public async Task AssignRoleAsync(Guid organizationId, Guid accountId, string roleKey, CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(RoleExistsSql, session.Connection, session.Transaction))
        {
            exists.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
            exists.Parameters.Add(new NpgsqlParameter<string> { TypedValue = roleKey });
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException($"The organization has no role '{roleKey}'.");
            }
        }

        await using var command = new NpgsqlCommand(AssignSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = roleKey });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RemoveMemberSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
