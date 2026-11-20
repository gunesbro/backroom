using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.CellManagement.Infrastructure.Migrations;

/// <summary>
/// The cell registry. Control-plane tables hold no organization content and are cross-organization by
/// nature, so they have no <c>org_id</c> and no row-level security (docs/adr/0005).
/// </summary>
[DbContext(typeof(CellManagementDbContext))]
[Migration("20261026120000_CreateCellRegistry")]
internal sealed class CreateCellRegistry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE cell_management.cells (
                id text PRIMARY KEY,
                name text NOT NULL,
                type text NOT NULL,
                region text NOT NULL,
                public_url text NOT NULL,
                status text NOT NULL CHECK (status IN ('pending', 'registered')),
                version text,
                created_at timestamptz NOT NULL,
                registered_at timestamptz,
                last_heartbeat_at timestamptz);

            CREATE TABLE cell_management.registration_keys (
                key_hash bytea PRIMARY KEY CHECK (octet_length(key_hash) = 32),
                cell_id text NOT NULL REFERENCES cell_management.cells (id),
                expires_at timestamptz NOT NULL,
                claimed_key_thumbprint text,
                claimed_at timestamptz,
                used_at timestamptz);

            CREATE INDEX registration_keys_cell_id_idx ON cell_management.registration_keys (cell_id);
            """);
    }
}
