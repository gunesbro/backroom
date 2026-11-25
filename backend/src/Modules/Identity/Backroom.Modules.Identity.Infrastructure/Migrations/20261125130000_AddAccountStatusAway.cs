using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Identity.Infrastructure.Migrations;

/// <summary>
/// Away chosen by hand (docs/contracts/phase-2.md, flow 9, owner decision of 2026-11-25): a flag next to do not disturb, never set
/// together with it. Adding a column with a constant default does not rewrite the table.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261125130000_AddAccountStatusAway")]
internal sealed class AddAccountStatusAway : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE identity.account_status
                ADD COLUMN away boolean NOT NULL DEFAULT false,
                ADD CONSTRAINT account_status_away_without_dnd CHECK (NOT (away AND dnd_until IS NOT NULL));
            """);
    }
}
