using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Migrations;

/// <summary>
/// The cell's end of link delivery (docs/contracts/phase-1.md, "Delivery"): the inbox de-duplicates
/// downstream messages; the outbox holds upstream messages until the control plane answers, then their
/// payload is deleted. The tables span organizations (a digest names many), so instead of row-level
/// security the application role may only insert; reading and settling need the relay role, which only
/// the worker connects as. Pending payloads can carry organization data (an invitee's email, a name).
/// </summary>
[DbContext(typeof(ControlPlaneConnectorDbContext))]
[Migration("20261030130000_CreateLinkTables")]
internal sealed class CreateLinkTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"""
            CREATE TABLE control_plane_connector.inbox (
                message_id uuid PRIMARY KEY,
                type text NOT NULL,
                received_at timestamptz NOT NULL);

            CREATE TABLE control_plane_connector.outbox (
                message_id uuid PRIMARY KEY,
                sequence bigint GENERATED ALWAYS AS IDENTITY,
                type text NOT NULL,
                payload bytea CHECK (octet_length(payload) <= 4194304),
                trace_parent text,
                trace_state text,
                created_at timestamptz NOT NULL,
                acked_at timestamptz,
                rejected_code text,
                rejected_at timestamptz,
                attempts integer NOT NULL DEFAULT 0,
                next_attempt_at timestamptz NOT NULL,
                CHECK ((acked_at IS NULL AND rejected_code IS NULL) = (payload IS NOT NULL)));

            -- The delivery poll: pending messages in sequence order.
            CREATE INDEX outbox_pending_idx ON control_plane_connector.outbox (sequence)
                WHERE acked_at IS NULL AND rejected_code IS NULL;

            REVOKE ALL ON control_plane_connector.inbox FROM {DatabaseRoles.Application};
            GRANT INSERT ON control_plane_connector.inbox TO {DatabaseRoles.Application};
            GRANT SELECT, DELETE ON control_plane_connector.inbox TO {DatabaseRoles.Relay};

            REVOKE ALL ON control_plane_connector.outbox FROM {DatabaseRoles.Application};
            GRANT INSERT ON control_plane_connector.outbox TO {DatabaseRoles.Application};
            GRANT SELECT, DELETE ON control_plane_connector.outbox TO {DatabaseRoles.Relay};
            GRANT UPDATE (payload, acked_at, rejected_code, rejected_at, attempts, next_attempt_at)
                ON control_plane_connector.outbox TO {DatabaseRoles.Relay};
            """);
    }
}
