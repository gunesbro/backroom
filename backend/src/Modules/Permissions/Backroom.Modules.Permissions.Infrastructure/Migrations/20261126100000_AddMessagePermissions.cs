using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Permissions.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 9 (docs/contracts/phase-2.md, "Permissions used in Phase 2"): the Admin system role of existing
/// organizations gets <c>messages.remove-others-public</c>. Owner needs no row: the checker gives it every permission.
/// New organizations get the key from <c>SystemRoles</c>. Custom roles are left alone. Nothing is removed on the way
/// down: the key means nothing to older code.
/// </summary>
[DbContext(typeof(PermissionsDbContext))]
[Migration("20261126100000_AddMessagePermissions")]
internal sealed class AddMessagePermissions : Migration
{
    internal static readonly string[] Tables = ["roles", "role_permissions"];

    internal const string InsertSql = """
        INSERT INTO permissions.role_permissions (org_id, role_id, permission)
        SELECT r.org_id, r.id, p.permission
        FROM permissions.roles r
        JOIN (VALUES ('admin', 'messages.remove-others-public')) AS p (role_key, permission)
            ON p.role_key = r.key
        WHERE r.is_system
        ON CONFLICT DO NOTHING
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.OrganizationDataSql(PermissionsDbContext.Schema, Tables, InsertSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
