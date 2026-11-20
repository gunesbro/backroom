using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.CellManagement.Infrastructure.Migrations;

/// <summary>Ids of upstream link messages already handled, so a resent message is handled once (docs/contracts/phase-1.md, "Delivery").</summary>
[DbContext(typeof(CellManagementDbContext))]
[Migration("20261030120000_CreateInboundMessages")]
internal sealed class CreateInboundMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE cell_management.inbound_messages (
                cell_id text NOT NULL REFERENCES cell_management.cells (id),
                message_id uuid NOT NULL,
                type text NOT NULL,
                received_at timestamptz NOT NULL,
                -- Per cell: another cell reusing an id can never suppress this cell's message.
                PRIMARY KEY (cell_id, message_id));
            """);
    }
}
