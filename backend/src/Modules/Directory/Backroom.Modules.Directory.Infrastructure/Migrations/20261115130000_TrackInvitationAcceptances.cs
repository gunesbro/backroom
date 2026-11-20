using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Directory.Infrastructure.Migrations;

/// <summary>
/// Accepting invitations (docs/contracts/phase-1.md, flow 3, steps 5 and 7): whether an acceptance made the account's
/// membership pending (so a refusal by the cell removes only a membership it added), and an index for an account's
/// latest acceptance, which the invite page polls.
/// </summary>
[DbContext(typeof(DirectoryDbContext))]
[Migration("20261115130000_TrackInvitationAcceptances")]
internal sealed class TrackInvitationAcceptances : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE directory.invitations ADD COLUMN added_membership boolean NOT NULL DEFAULT false;

            -- GET /api/v1/invitations/acceptance: an account's newest acceptance. Only acceptances carry the account.
            CREATE INDEX invitations_accepting_account_idx ON directory.invitations (accepting_account_id, accepted_at DESC, invitation_id DESC)
                WHERE accepting_account_id IS NOT NULL;
            """);
    }
}
