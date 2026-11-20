using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Refuses to work with a database role that can switch row-level security off: a superuser, a role
/// with BYPASSRLS, the owner of a schema or table outside the system schemas (an owner can disable,
/// un-force or drop the policies), a role that can create roles or use the server's files and
/// programs, or a member of any of these (it could <c>SET ROLE</c>).
/// This catches, for example, a deployment configured with the administrator's or the migrator's
/// connection string. Checked once per process, on the first unit of work.
/// </summary>
public sealed class DatabaseRoleGuard
{
    /// <summary>True when the current role is, or is a member of, a superuser.</summary>
    internal const string MemberOfSuperuserSql = """
        EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
                WHERE r.rolsuper AND pg_catalog.pg_has_role(current_user, r.oid, 'MEMBER'))
        """;

    /// <summary>True when the current role is, or is a member of, a role with BYPASSRLS.</summary>
    internal const string MemberOfBypassRlsSql = """
        EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
                WHERE r.rolbypassrls AND pg_catalog.pg_has_role(current_user, r.oid, 'MEMBER'))
        """;

    /// <summary>True when the current role, or a role it is a member of, owns a non-system schema or table.</summary>
    internal const string OwnsObjectsSql = """
        (EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
                 WHERE n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
                   AND n.nspowner <> 'pg_database_owner'::regrole
                   AND pg_catalog.pg_has_role(current_user, n.nspowner, 'MEMBER'))
         OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
                      AND pg_catalog.pg_has_role(current_user, c.relowner, 'MEMBER')))
        """;

    /// <summary>
    /// True when the current role, or a role it is a member of, can create roles or read, write or run
    /// programs on the database server.
    /// </summary>
    internal const string HoldsServerPrivilegesSql = """
        EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
                WHERE (r.rolcreaterole OR r.rolname IN ('pg_execute_server_program', 'pg_write_server_files', 'pg_read_server_files'))
                  AND pg_catalog.pg_has_role(current_user, r.oid, 'MEMBER'))
        """;

    /// <summary>True when the current role is a member of another role, or another role is a member of it.</summary>
    internal const string HasRoleMembershipsSql = """
        EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m
                JOIN pg_catalog.pg_roles r ON r.rolname = current_user
                WHERE m.member = r.oid OR m.roleid = r.oid)
        """;

    /// <summary>
    /// True when the current role is fit for a dedicated cross-organization role (the outbox relay, the
    /// maintenance jobs): it has BYPASSRLS itself (without it, it would silently see no rows), but is
    /// neither (a member of) a superuser nor (a member of) an owner of database objects (an owner could read
    /// and change everything), holds no server privileges, and has no role memberships in either direction
    /// (it could <c>SET ROLE</c>, or a member could use its privileges).
    /// </summary>
    internal const string DedicatedBypassRoleSql =
        "SELECT rolbypassrls AND NOT " + MemberOfSuperuserSql + " AND NOT " + OwnsObjectsSql + " AND NOT " + HoldsServerPrivilegesSql +
        " AND NOT " + HasRoleMembershipsSql + " FROM pg_catalog.pg_roles WHERE rolname = current_user";

    private const string BypassesRowSecuritySql =
        "SELECT " + MemberOfSuperuserSql + " OR " + MemberOfBypassRlsSql + " OR " + OwnsObjectsSql + " OR " + HoldsServerPrivilegesSql;

    private volatile bool _verified;

    public async Task EnsureRoleCannotBypassRowSecurityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_verified)
        {
            return;
        }

        await using var command = new NpgsqlCommand(BypassesRowSecuritySql, connection);
        var bypasses = await command.ExecuteScalarAsync(cancellationToken) is true;
        if (bypasses)
        {
            throw new InvalidOperationException(
                "The database role bypasses row-level security or owns database objects. Connect with the application role, which has neither SUPERUSER nor BYPASSRLS and owns nothing.");
        }

        _verified = true;
    }

    /// <summary>Whether the connection's role passes <see cref="DedicatedBypassRoleSql"/>.</summary>
    internal static async Task<bool> IsDedicatedBypassRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(DedicatedBypassRoleSql, connection);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}
