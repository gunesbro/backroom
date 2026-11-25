using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Permissions.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 7 (docs/contracts/phase-2.md, "Permissions used in Phase 2"): the system roles of existing
/// organizations get <c>channels.create-private</c> (Admin, Member) and <c>channels.manage-public</c> (Admin). Owner
/// needs no row: the checker gives it every permission. New organizations get the keys from <c>SystemRoles</c>.
/// Custom roles are left alone. Nothing is removed on the way down: the keys mean nothing to older code.
/// </summary>
[DbContext(typeof(PermissionsDbContext))]
[Migration("20261125150000_AddChannelPermissions")]
internal sealed class AddChannelPermissions : Migration
{
    internal static readonly string[] Tables = ["roles", "role_permissions"];

    internal const string InsertSql = """
        INSERT INTO permissions.role_permissions (org_id, role_id, permission)
        SELECT r.org_id, r.id, p.permission
        FROM permissions.roles r
        JOIN (VALUES ('admin', 'channels.create-private'), ('admin', 'channels.manage-public'), ('member', 'channels.create-private')) AS p (role_key, permission)
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
