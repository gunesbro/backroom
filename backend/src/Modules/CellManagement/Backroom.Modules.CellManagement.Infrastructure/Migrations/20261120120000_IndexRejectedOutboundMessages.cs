using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.CellManagement.Infrastructure.Migrations;

/// <summary>
/// The operations panel counts each cell's rejected messages. Pending ones are covered by
/// <c>outbound_messages_pending_idx</c>; rejected ones are rare and this partial index keeps that count small.
/// </summary>
[DbContext(typeof(CellManagementDbContext))]
[Migration("20261120120000_IndexRejectedOutboundMessages")]
internal sealed class IndexRejectedOutboundMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE INDEX outbound_messages_rejected_idx ON cell_management.outbound_messages (cell_id)
                WHERE rejected_code IS NOT NULL;
            """);
    }
}
