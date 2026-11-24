using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Organization.Infrastructure.Migrations;

/// <summary>
/// Repeats the marker backfill of <see cref="EraseInvitationEmailsByMarker"/>, which ran as the migrator under
/// forced row-level security without an organization context and so matched no row: invitations whose email was
/// already erased kept <c>email_erased_at</c> NULL. Idempotent (it only touches rows still unmarked). No email was
/// exposed by the miss: those rows hold no email, and the retention job would have stamped them in its next run.
/// </summary>
[DbContext(typeof(OrganizationDbContext))]
[Migration("20261124130000_BackfillInvitationErasedMarkers")]
internal sealed class BackfillInvitationErasedMarkers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.OrganizationDataSql(OrganizationDbContext.Schema, ["invitations"], EraseInvitationEmailsByMarker.BackfillSql);
    }

    /// <summary>Nothing to undo: the markers are correct under either schema version.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
