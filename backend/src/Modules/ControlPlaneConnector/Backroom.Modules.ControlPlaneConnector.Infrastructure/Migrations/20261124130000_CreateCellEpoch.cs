using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Migrations;

/// <summary>
/// The cell's copy of its epoch (docs/adr/0011, "Cell epoch"). The control plane holds the epoch; the worker stores
/// what <c>Welcome</c> carried and marks it confirmed, and clears the confirmation when the control plane ends the
/// stream because the epoch changed. The app role reads it to fence writes and cannot change it; only the worker's
/// relay role writes it. The restore and failover procedure clears <c>confirmed</c> in the restored database before
/// the cell starts (docs/runbooks/cell-restore-and-failover.md). One row per cell id, so a database restored into
/// another cell confirms nothing. No organization data, so no row-level security.
/// </summary>
[DbContext(typeof(ControlPlaneConnectorDbContext))]
[Migration("20261124130000_CreateCellEpoch")]
internal sealed class CreateCellEpoch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"""
            CREATE TABLE control_plane_connector.cell_epoch (
                cell_id text PRIMARY KEY,
                epoch bigint NOT NULL CHECK (epoch > 0),
                confirmed boolean NOT NULL,
                updated_at timestamptz NOT NULL);

            REVOKE ALL ON control_plane_connector.cell_epoch FROM {DatabaseRoles.Application};
            GRANT SELECT ON control_plane_connector.cell_epoch TO {DatabaseRoles.Application};
            GRANT SELECT, INSERT ON control_plane_connector.cell_epoch TO {DatabaseRoles.Relay};
            GRANT UPDATE (epoch, confirmed, updated_at) ON control_plane_connector.cell_epoch TO {DatabaseRoles.Relay};
            """);
    }
}
