using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.CellManagement.Infrastructure.Migrations;

/// <summary>
/// Durable downstream link messages (docs/contracts/phase-1.md, "Delivery"). A message is pending until
/// acknowledged or rejected; then its payload is deleted (it may carry a name and an email). A message
/// sent on a stream is leased by that stream until its answer is due, so the poll does not send it again
/// meanwhile. <c>cell_answer</c> keeps the cell's first answer (<c>:acknowledged</c> or its rejection code)
/// while our handler still fails, so the message is not sent again and the answer is not lost.
/// </summary>
[DbContext(typeof(CellManagementDbContext))]
[Migration("20261029120000_CreateOutboundMessages")]
internal sealed class CreateOutboundMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE cell_management.outbound_messages (
                message_id uuid PRIMARY KEY,
                cell_id text NOT NULL REFERENCES cell_management.cells (id),
                sequence bigint GENERATED ALWAYS AS IDENTITY,
                type text NOT NULL,
                payload bytea CHECK (octet_length(payload) <= 65536),
                trace_parent text,
                trace_state text,
                created_at timestamptz NOT NULL,
                acked_at timestamptz,
                rejected_code text,
                rejected_at timestamptz,
                attempts integer NOT NULL DEFAULT 0,
                next_attempt_at timestamptz NOT NULL,
                leased_until timestamptz,
                leased_by uuid,
                cell_answer text,
                CHECK ((acked_at IS NULL AND rejected_code IS NULL) = (payload IS NOT NULL)));

            -- The delivery poll: a cell's pending messages in sequence order.
            CREATE INDEX outbound_messages_pending_idx ON cell_management.outbound_messages (cell_id, sequence)
                WHERE acked_at IS NULL AND rejected_code IS NULL;
            """);
    }
}
