using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Organization.Infrastructure.Migrations;

/// <summary>
/// The scheduled invitation jobs (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and retention"): the
/// time an invitation was marked expired (the retention of its email counts from it), one partial index per job
/// holding only the rows that job still has to visit, and the relay role's privileges. The jobs work across all
/// organizations, so they run as the relay role (BYPASSRLS, owns nothing), like the outbox relay and the link
/// delivery; it gets only the columns the jobs read and change.
/// </summary>
[DbContext(typeof(OrganizationDbContext))]
[Migration("20261115150000_IndexInvitationJobs")]
internal sealed class IndexInvitationJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"""
            ALTER TABLE organization.invitations ADD COLUMN expired_at timestamptz;

            -- Expiry: pending invitations by expiry.
            CREATE INDEX invitations_pending_expiry_idx ON organization.invitations (expires_at) WHERE status = 'pending';

            -- Retention: expired and revoked invitations that still hold the email, by when they reached that state.
            CREATE INDEX invitations_expired_email_idx ON organization.invitations (expired_at)
                WHERE status = 'expired' AND email IS NOT NULL;
            CREATE INDEX invitations_revoked_email_idx ON organization.invitations (revoked_at)
                WHERE status = 'revoked' AND email IS NOT NULL;

            GRANT SELECT (id, org_id, status, expires_at, expired_at, revoked_at, email) ON organization.invitations TO {DatabaseRoles.Relay};
            GRANT UPDATE (status, expired_at, email) ON organization.invitations TO {DatabaseRoles.Relay};
            """);
    }
}
