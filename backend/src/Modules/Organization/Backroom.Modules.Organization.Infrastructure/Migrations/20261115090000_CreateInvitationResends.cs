using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Organization.Infrastructure.Migrations;

/// <summary>
/// Resends of invitations, so they count towards the daily limits like new invitations (docs/contracts/phase-1.md,
/// flow 3, step 1). The recipient is read through the invitation; a row holds only the resender's account id
/// (pseudonymous personal data), and the retention job deletes rows once they are past the limits' window.
/// </summary>
[DbContext(typeof(OrganizationDbContext))]
[Migration("20261115090000_CreateInvitationResends")]
internal sealed class CreateInvitationResends : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            -- A resend and its invitation stay in one organization, although foreign-key checks ignore row security.
            ALTER TABLE organization.invitations ADD CONSTRAINT invitations_org_id_id_key UNIQUE (org_id, id);

            CREATE TABLE organization.invitation_resends (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                invitation_id uuid NOT NULL,
                resent_by uuid NOT NULL,
                resent_at timestamptz NOT NULL,
                FOREIGN KEY (org_id, invitation_id) REFERENCES organization.invitations (org_id, id) ON DELETE CASCADE);

            -- The daily limits count an organization's resends of the last 24 hours.
            CREATE INDEX invitation_resends_resent_at_idx ON organization.invitation_resends (org_id, resent_at);

            -- Deleting an invitation finds its resends without scanning the table.
            CREATE INDEX invitation_resends_invitation_idx ON organization.invitation_resends (org_id, invitation_id);
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(OrganizationDbContext.Schema, "invitation_resends");
    }
}
