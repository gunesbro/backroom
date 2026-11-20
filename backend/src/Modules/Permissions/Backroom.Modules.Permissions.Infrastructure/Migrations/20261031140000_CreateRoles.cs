using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Permissions.Infrastructure.Migrations;

/// <summary>Roles, their permissions and members' roles per organization, each table under row-level security.</summary>
[DbContext(typeof(PermissionsDbContext))]
[Migration("20261031140000_CreateRoles")]
internal sealed class CreateRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE permissions.roles (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                key text NOT NULL CHECK (char_length(key) BETWEEN 1 AND 64),
                is_system boolean NOT NULL,
                UNIQUE (org_id, key));

            CREATE TABLE permissions.role_permissions (
                org_id uuid NOT NULL,
                role_id uuid NOT NULL REFERENCES permissions.roles (id),
                permission text NOT NULL CHECK (char_length(permission) BETWEEN 1 AND 128),
                PRIMARY KEY (role_id, permission));

            CREATE TABLE permissions.member_roles (
                org_id uuid NOT NULL,
                account_id uuid NOT NULL,
                role_id uuid NOT NULL REFERENCES permissions.roles (id),
                PRIMARY KEY (org_id, account_id, role_id));

            CREATE INDEX member_roles_role_id_idx ON permissions.member_roles (role_id);
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(PermissionsDbContext.Schema, "roles");
        migrationBuilder.EnableOrganizationRowLevelSecurity(PermissionsDbContext.Schema, "role_permissions");
        migrationBuilder.EnableOrganizationRowLevelSecurity(PermissionsDbContext.Schema, "member_roles");
    }
}
