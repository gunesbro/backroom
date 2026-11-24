using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Identity.Infrastructure.Migrations;

/// <summary>
/// Sessions Keycloak ended (docs/contracts/phase-1.md, "Data"). Control-plane tables hold no organization content, so there
/// is no row-level security (docs/adr/0005). An empty <c>session_id</c> means every session of the account; the primary key
/// makes a repeated logout of one session a conflict. Rows are deleted after <c>keep_until</c>, by the cleanup job.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration("20261124120000_CreateRevokedSessions")]
internal sealed class CreateRevokedSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE identity.revoked_sessions (
                session_id text NOT NULL CHECK (char_length(session_id) <= 128),
                account_id uuid NOT NULL,
                revoked_at timestamptz NOT NULL,
                keep_until timestamptz NOT NULL,
                PRIMARY KEY (session_id, account_id));

            -- The cleanup job and FullSync both read by keep_until.
            CREATE INDEX revoked_sessions_keep_until_idx ON identity.revoked_sessions (keep_until);
            """);
    }
}
