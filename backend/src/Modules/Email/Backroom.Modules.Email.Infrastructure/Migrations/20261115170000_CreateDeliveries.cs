using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Email.Infrastructure.Migrations;

/// <summary>
/// The delivery log (docs/contracts/phase-1.md, "Data") and the module's inbox for its event consumers, both under
/// row-level security. A delivery holds the template, a keyed hash of the recipient (32 bytes), the provider's
/// message id and the status: no address, subject, body or link, so there is no column to put them in. Rows are
/// kept 90 days (the retention job uses the <c>created_at</c> index). The application role records and updates
/// deliveries but cannot delete them.
/// </summary>
[DbContext(typeof(EmailDbContext))]
[Migration("20261115170000_CreateDeliveries")]
internal sealed class CreateDeliveries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($$"""
            CREATE TABLE email.deliveries (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                template text NOT NULL CHECK (template ~ '^[a-z0-9-]{1,64}$'),
                template_version integer NOT NULL CHECK (template_version > 0),
                recipient_hash bytea NOT NULL CHECK (octet_length(recipient_hash) = 32),
                provider_message_id text CHECK (char_length(provider_message_id) BETWEEN 1 AND 255),
                status text NOT NULL CHECK (status IN ('pending', 'sent', 'failed')),
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL);

            -- Retention (90 days) and an organization's recent deliveries.
            CREATE INDEX deliveries_created_at_idx ON email.deliveries (created_at);
            CREATE INDEX deliveries_org_created_at_idx ON email.deliveries (org_id, created_at);

            REVOKE DELETE, TRUNCATE ON email.deliveries FROM {{DatabaseRoles.Application}};
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(EmailDbContext.Schema, "deliveries");
        migrationBuilder.CreateInboxTable(EmailDbContext.Schema);
    }
}
