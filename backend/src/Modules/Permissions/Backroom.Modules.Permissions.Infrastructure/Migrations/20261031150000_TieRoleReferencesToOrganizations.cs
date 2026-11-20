using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Permissions.Infrastructure.Migrations;

/// <summary>
/// Foreign-key checks ignore row-level security, so a reference to a role by id alone could tie one
/// organization's row to another organization's role. Composite keys <c>(org_id, role_id)</c> keep a row and
/// its role in one organization.
/// </summary>
[DbContext(typeof(PermissionsDbContext))]
[Migration("20261031150000_TieRoleReferencesToOrganizations")]
internal sealed class TieRoleReferencesToOrganizations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE permissions.roles ADD CONSTRAINT roles_org_id_id_key UNIQUE (org_id, id);

            ALTER TABLE permissions.role_permissions DROP CONSTRAINT role_permissions_role_id_fkey;
            ALTER TABLE permissions.role_permissions ADD CONSTRAINT role_permissions_role_fkey
                FOREIGN KEY (org_id, role_id) REFERENCES permissions.roles (org_id, id);

            ALTER TABLE permissions.member_roles DROP CONSTRAINT member_roles_role_id_fkey;
            ALTER TABLE permissions.member_roles ADD CONSTRAINT member_roles_role_fkey
                FOREIGN KEY (org_id, role_id) REFERENCES permissions.roles (org_id, id);
            """);
    }
}
