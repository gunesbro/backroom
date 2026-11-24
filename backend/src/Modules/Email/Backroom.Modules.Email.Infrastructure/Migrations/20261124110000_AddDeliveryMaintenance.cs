using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Email.Infrastructure.Migrations;

/// <summary>
/// The delivery log's maintenance jobs (docs/adr/0010): <c>email.delivery-stale-pending</c> and <c>email.delivery-retention</c>.
/// <list type="bullet">
/// <item>New terminal status <c>unknown</c> (the outcome is unknown, like <c>EmailSendFailure.Unknown</c>): a stale
/// <c>pending</c> row is closed as <c>unknown</c>, never as <c>failed</c>. The partial unique index
/// <c>deliveries_reference_once_idx</c> (<c>WHERE status &lt;&gt; 'failed'</c>) and the <c>ON CONFLICT</c> predicate of
/// <c>DeliveryLog</c> therefore keep blocking a second send for that reference.</item>
/// <item>A partial index on <c>updated_at</c> of pending rows for the stale job. Retention uses the existing
/// <c>deliveries_created_at_idx</c>: its filter has no state to make a partial index of.</item>
/// <item>Grants for the maintenance role, columns only: <c>SELECT</c> of <c>id</c>, <c>status</c>, <c>created_at</c> and
/// <c>updated_at</c> (never <c>recipient_hash</c>, <c>provider_message_id</c> or the reference), <c>UPDATE</c> of
/// <c>status</c> and <c>updated_at</c> (also what <c>FOR UPDATE</c> needs), and <c>DELETE</c> for retention.</item>
/// <item>Cascades: <c>email.deliveries</c> has no foreign key in either direction (the inbox is a separate table), so a
/// delete reaches no other table.</item>
/// </list>
/// </summary>
[DbContext(typeof(EmailDbContext))]
[Migration("20261124110000_AddDeliveryMaintenance")]
internal sealed class AddDeliveryMaintenance : Migration
{
    private static readonly string[] s_selectColumns = ["id", "status", "created_at", "updated_at"];
    private static readonly string[] s_updateColumns = ["status", "updated_at"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE email.deliveries DROP CONSTRAINT deliveries_status_check;
            ALTER TABLE email.deliveries ADD CONSTRAINT deliveries_status_check CHECK (status IN ('pending', 'sent', 'failed', 'unknown'));

            -- Stale pending rows, oldest first.
            CREATE INDEX deliveries_pending_idx ON email.deliveries (updated_at) WHERE status = 'pending';
            """);
        migrationBuilder.GrantMaintenanceColumns(EmailDbContext.Schema, "deliveries", s_selectColumns, s_updateColumns);
        migrationBuilder.GrantMaintenanceDelete(EmailDbContext.Schema, "deliveries");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.RevokeMaintenanceGrants(EmailDbContext.Schema, "deliveries");
        migrationBuilder.Sql("""
            DROP INDEX email.deliveries_pending_idx;
            """);
        migrationBuilder.OrganizationDataSql(EmailDbContext.Schema, ["deliveries"], "UPDATE email.deliveries SET status = 'pending' WHERE status = 'unknown';");
        migrationBuilder.Sql("""
            ALTER TABLE email.deliveries DROP CONSTRAINT deliveries_status_check;
            ALTER TABLE email.deliveries ADD CONSTRAINT deliveries_status_check CHECK (status IN ('pending', 'sent', 'failed'));
            """);
    }
}
