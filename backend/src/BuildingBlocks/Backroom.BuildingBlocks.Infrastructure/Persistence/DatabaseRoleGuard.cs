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
}
