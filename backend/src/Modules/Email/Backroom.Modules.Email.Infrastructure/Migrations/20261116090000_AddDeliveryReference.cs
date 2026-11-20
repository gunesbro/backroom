using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Email.Infrastructure.Migrations;

/// <summary>
/// What a delivery is for (an invitation and its code version), as ids only. At most one delivery of a template name
/// per reference may be pending or sent, so a redelivered event cannot send a second email; failed deliveries stay
/// as history and do not block a retry. The index also answers "the delivery of this invitation" (Phase 2).
/// </summary>
[DbContext(typeof(EmailDbContext))]
[Migration("20261116090000_AddDeliveryReference")]
internal sealed class AddDeliveryReference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE email.deliveries
                ADD COLUMN reference_id uuid,
                ADD COLUMN reference_version integer,
                ADD CONSTRAINT deliveries_reference_check CHECK (
                    (reference_id IS NULL) = (reference_version IS NULL) AND (reference_version IS NULL OR reference_version > 0));
            CREATE UNIQUE INDEX deliveries_reference_once_idx ON email.deliveries (org_id, template, reference_id, reference_version)
                WHERE reference_id IS NOT NULL AND status <> 'failed';
            """);
    }
}
