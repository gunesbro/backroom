using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Organization.Infrastructure.Migrations;

/// <summary>
/// The cell's organizations and members (docs/contracts/phase-1.md, "Data"), each table under row-level
/// security. An organization's row carries <c>org_id</c> equal to its id, so the same policy applies.
/// </summary>
[DbContext(typeof(OrganizationDbContext))]
[Migration("20261031140000_CreateOrganizations")]
internal sealed class CreateOrganizations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE organization.organizations (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL CHECK (org_id = id),
                name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 400),
                owner_account_id uuid NOT NULL,
                version bigint NOT NULL,
                created_at timestamptz NOT NULL);

            CREATE TABLE organization.members (
                org_id uuid NOT NULL REFERENCES organization.organizations (id),
                account_id uuid NOT NULL,
                display_name text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 400),
                email text NOT NULL CHECK (char_length(email) BETWEEN 3 AND 254),
                locale text NOT NULL CHECK (char_length(locale) BETWEEN 2 AND 35),
                status text NOT NULL CHECK (status IN ('active', 'deactivated', 'removed')),
                version bigint NOT NULL,
                joined_at timestamptz NOT NULL,
                PRIMARY KEY (org_id, account_id));

            -- An account's memberships across organizations (organization context checks, digests).
            CREATE INDEX members_account_id_idx ON organization.members (account_id);
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(OrganizationDbContext.Schema, "organizations");
        migrationBuilder.EnableOrganizationRowLevelSecurity(OrganizationDbContext.Schema, "members");
    }
}
