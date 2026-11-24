using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Organization.Infrastructure.Migrations;

/// <summary>
/// Moves the invitation jobs from the relay role to the maintenance role (docs/adr/0010). Retention finds its rows
/// through the new marker <c>email_erased_at</c> instead of reading <c>email</c>, so the job's indexes no longer
/// depend on the email and the maintenance role gets no <c>SELECT</c> on it: <c>SELECT</c> of the state, time and
/// key columns the queries reference, <c>UPDATE</c> of <c>status</c>, <c>expired_at</c>, <c>email</c> and
/// <c>email_erased_at</c> (<c>FOR UPDATE</c> needs <c>UPDATE</c> on at least one column). The relay role keeps only
/// its outbox and inbox privileges. Rows whose email is already NULL are marked erased.
/// </summary>
[DbContext(typeof(OrganizationDbContext))]
[Migration("20261124100000_EraseInvitationEmailsByMarker")]
internal sealed class EraseInvitationEmailsByMarker : Migration
{
    /// <summary>Marks rows whose email was already erased: at the time they reached the state, otherwise now.</summary>
    internal const string BackfillSql = """
        UPDATE organization.invitations
        SET email_erased_at = COALESCE(CASE status WHEN 'expired' THEN expired_at WHEN 'revoked' THEN revoked_at END, now())
        WHERE email IS NULL AND email_erased_at IS NULL;
        """;

    private static readonly string[] s_selectColumns = ["id", "org_id", "status", "expires_at", "expired_at", "revoked_at", "email_erased_at"];
    private static readonly string[] s_updateColumns = ["status", "expired_at", "email", "email_erased_at"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"""
            ALTER TABLE organization.invitations ADD COLUMN email_erased_at timestamptz;

            {BackfillSql}

            -- Retention: expired and revoked invitations whose email is not erased yet, by when they reached that state.
            DROP INDEX organization.invitations_expired_email_idx;
            DROP INDEX organization.invitations_revoked_email_idx;
            CREATE INDEX invitations_expired_email_idx ON organization.invitations (expired_at)
                WHERE status = 'expired' AND email_erased_at IS NULL;
            CREATE INDEX invitations_revoked_email_idx ON organization.invitations (revoked_at)
                WHERE status = 'revoked' AND email_erased_at IS NULL;

            REVOKE ALL ON organization.invitations FROM {DatabaseRoles.Relay};
            """);
        migrationBuilder.GrantMaintenanceColumns("organization", "invitations", s_selectColumns, s_updateColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.RevokeMaintenanceGrants("organization", "invitations");
        migrationBuilder.Sql($"""
            DROP INDEX organization.invitations_expired_email_idx;
            DROP INDEX organization.invitations_revoked_email_idx;
            ALTER TABLE organization.invitations DROP COLUMN email_erased_at;
            CREATE INDEX invitations_expired_email_idx ON organization.invitations (expired_at)
                WHERE status = 'expired' AND email IS NOT NULL;
            CREATE INDEX invitations_revoked_email_idx ON organization.invitations (revoked_at)
                WHERE status = 'revoked' AND email IS NOT NULL;

            GRANT SELECT (id, org_id, status, expires_at, expired_at, revoked_at, email) ON organization.invitations TO {DatabaseRoles.Relay};
            GRANT UPDATE (status, expired_at, email) ON organization.invitations TO {DatabaseRoles.Relay};
            """);
    }
}
