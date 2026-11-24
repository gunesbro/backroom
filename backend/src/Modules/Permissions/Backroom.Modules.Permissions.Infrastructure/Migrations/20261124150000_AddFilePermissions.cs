using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Permissions.Infrastructure.Migrations;

/// <summary>
/// Gives existing organizations' system roles the file permissions of docs/contracts/phase-2.md ("Permissions used in
/// Phase 2": Admin <c>files.upload</c> and <c>files.remove-others-public</c>, Member <c>files.upload</c>); new
/// organizations get them from <c>SystemRoles</c>. Owner needs no row: the checker treats it as having every permission.
/// Runs through <see cref="OrganizationDataMigrationExtensions.OrganizationDataSql"/>, because the tables force
/// row-level security on their owner too; each row keeps the organization of its role. Idempotent.
/// </summary>
[DbContext(typeof(PermissionsDbContext))]
[Migration("20261124150000_AddFilePermissions")]
internal sealed class AddFilePermissions : Migration
{
    internal static readonly string[] Tables = ["roles", "role_permissions"];

    internal const string InsertSql = """
        INSERT INTO permissions.role_permissions (org_id, role_id, permission)
        SELECT r.org_id, r.id, p.permission
        FROM permissions.roles r
        JOIN (VALUES ('admin', 'files.upload'), ('admin', 'files.remove-others-public'), ('member', 'files.upload')) AS p (role_key, permission)
            ON p.role_key = r.key
        WHERE r.is_system
        ON CONFLICT DO NOTHING
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.OrganizationDataSql(PermissionsDbContext.Schema, Tables, InsertSql);
    }

    /// <summary>Nothing to undo: the grants match the contract's defaults.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
