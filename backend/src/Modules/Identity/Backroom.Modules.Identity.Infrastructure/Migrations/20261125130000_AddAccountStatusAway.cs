using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Identity.Infrastructure.Migrations;

/// <summary>
/// Away chosen by hand (docs/contracts/phase-2.md, flow 9, owner decision of 2026-11-25): a flag next to do not disturb. Adding a
/// column with a constant default does not rewrite the table. There is deliberately no check that forbids away together with do not
/// disturb: during a rolling deploy an instance built before this column replaces <c>dnd_until</c> without touching <c>away</c>, and
/// such a check would refuse that write. The rule lives in the application instead (<c>AccountStatusRules</c> never stores both), and
/// the store reads away as off while the row has a do-not-disturb time, so a row an older instance left behind is still read right.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261125130000_AddAccountStatusAway")]
internal sealed class AddAccountStatusAway : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE identity.account_status ADD COLUMN away boolean NOT NULL DEFAULT false;
            """);
    }
}
