using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Permissions.Infrastructure.Migrations;

/// <summary>
/// Gives existing organizations' system roles the file permissions of docs/contracts/phase-2.md ("Permissions used in
/// Phase 2": Admin <c>files.upload</c> and <c>files.remove-others-public</c>, Member <c>files.upload</c>); new
/// organizations get them from <c>SystemRoles</c>. Owner needs no row: the checker treats it as having every permission.
/// </summary>
/// <remarks>
/// The tables force row-level security, which applies to their owner too, and a migration has no organization
/// context. The owner lifts FORCE for this one statement and restores it in the same transaction, so no other session
/// ever sees the tables without it; a failure rolls everything back.
/// </remarks>
[DbContext(typeof(PermissionsDbContext))]
[Migration("20261124150000_AddFilePermissions")]
internal sealed class AddFilePermissions : Migration
{
    internal const string BackfillSql = """
        ALTER TABLE permissions.roles NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE permissions.role_permissions NO FORCE ROW LEVEL SECURITY;

        INSERT INTO permissions.role_permissions (org_id, role_id, permission)
        SELECT r.org_id, r.id, p.permission
        FROM permissions.roles r
        JOIN (VALUES ('admin', 'files.upload'), ('admin', 'files.remove-others-public'), ('member', 'files.upload')) AS p (role_key, permission)
            ON p.role_key = r.key
        WHERE r.is_system
        ON CONFLICT DO NOTHING;

        ALTER TABLE permissions.roles FORCE ROW LEVEL SECURITY;
        ALTER TABLE permissions.role_permissions FORCE ROW LEVEL SECURITY;
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(BackfillSql);
    }
}
