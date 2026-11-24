using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.CellManagement.Infrastructure.Migrations;

/// <summary>
/// The cell epoch (docs/adr/0011, "Cell epoch"): the control plane holds it, because a value kept in the cell's own
/// database would come back unchanged with a restore or a promoted replica. The restore and failover procedure
/// increments it (<c>POST /api/v1/ops/cells/{id}/epoch</c>); <c>Welcome</c> carries it to the cell.
/// </summary>
[DbContext(typeof(CellManagementDbContext))]
[Migration("20261124120000_AddCellEpoch")]
internal sealed class AddCellEpoch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE cell_management.cells ADD COLUMN epoch bigint NOT NULL DEFAULT 1 CHECK (epoch > 0);
            """);
    }
}
