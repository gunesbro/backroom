using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Directory.Infrastructure.Migrations;

/// <summary>
/// The scheduled invitation jobs (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and retention"). Each
/// index is partial: it holds only the rows its job still has to visit, so it stays small and a job's batch never
/// scans ended or cleaned invitations.
/// </summary>
[DbContext(typeof(DirectoryDbContext))]
[Migration("20261115140000_IndexInvitationJobs")]
internal sealed class IndexInvitationJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            -- Expiry: pending invitations by expiry.
            CREATE INDEX invitations_pending_expiry_idx ON directory.invitations (expires_at) WHERE status = 'pending';

            -- Retention: ended and used invitations that still have the email HMAC, by when they ended or were used (updated_at).
            CREATE INDEX invitations_ended_email_idx ON directory.invitations (updated_at)
                WHERE email_hmac IS NOT NULL AND status IN ('expired', 'revoked', 'failed', 'used');

            -- Retention: revocation tombstones (the only rows without a code version) by revocation.
            CREATE INDEX invitations_tombstone_idx ON directory.invitations (revoked_at)
                WHERE status = 'revoked' AND code_version IS NULL;
            """);
    }
}
