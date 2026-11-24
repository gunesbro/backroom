using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Identity.Infrastructure.Migrations;

/// <summary>
/// Account statuses (docs/contracts/phase-2.md, "Data"): one row per account that ever set a status. Account-level data without
/// organization content, so no row-level security (docs/adr/0005). No history: a change replaces the row. The checks repeat the
/// application's limits (text at most 100 characters of at most four code points each; one emoji of at most 16 code points).
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261125120000_CreateAccountStatus")]
internal sealed class CreateAccountStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE identity.account_status (
                account_id uuid PRIMARY KEY,
                dnd_until timestamptz,
                custom_emoji text CHECK (char_length(custom_emoji) BETWEEN 1 AND 16),
                custom_text text CHECK (char_length(custom_text) BETWEEN 1 AND 400),
                custom_expires_at timestamptz,
                time_zone text CHECK (char_length(time_zone) BETWEEN 1 AND 64),
                version bigint NOT NULL CHECK (version > 0),
                updated_at timestamptz NOT NULL);
            """);
    }
}
