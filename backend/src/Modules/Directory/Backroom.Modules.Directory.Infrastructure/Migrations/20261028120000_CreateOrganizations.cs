using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Directory.Infrastructure.Migrations;

/// <summary>
/// Organizations, their cells and the membership view. Control-plane tables hold no organization
/// content and are cross-organization by nature, so they have no row-level security (docs/adr/0005);
/// every query filters by the authenticated account.
/// </summary>
[DbContext(typeof(DirectoryDbContext))]
[Migration("20261028120000_CreateOrganizations")]
internal sealed class CreateOrganizations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE directory.organizations (
                id uuid PRIMARY KEY,
                name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 100),
                region text NOT NULL,
                cell_id text NOT NULL,
                status text NOT NULL CHECK (status IN ('provisioning', 'active', 'failed')),
                failure_code text,
                created_by uuid NOT NULL,
                created_at timestamptz NOT NULL);

            -- The daily creation limit per account.
            CREATE INDEX organizations_created_by_idx ON directory.organizations (created_by, created_at);

            CREATE TABLE directory.memberships (
                account_id uuid NOT NULL,
                org_id uuid NOT NULL REFERENCES directory.organizations (id),
                status text NOT NULL CHECK (status IN ('pending', 'active', 'removed')),
                version bigint NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (account_id, org_id));

            -- Membership snapshots and digests per organization.
            CREATE INDEX memberships_org_id_idx ON directory.memberships (org_id);
            """);
    }
}
