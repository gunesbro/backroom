using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 10 (docs/contracts/phase-2.md, flow 2 "Reactions" and "Data"): <c>messaging.reactions</c> and the message's stored
/// summary <c>messages.reaction_summary</c>.
/// <list type="bullet">
/// <item><c>reactions</c>: one row per (message, person, emoji), so a repeat is one row. The primary key
/// <c>(message_id, account_id, emoji)</c> serves everything: the person's count and "already reacted" check (prefix of two columns),
/// the aggregate that rebuilds a message's summary (prefix of one), and <c>reactedByMe</c> for a page (message ids as an array, one
/// account). The emoji is at most 32 bytes (the application also validates its form). The foreign key to <c>messages (id)</c> is
/// single-column like <c>message_mentions</c> and <c>message_links</c>: <c>messages</c> has no <c>(org_id, id)</c> key, and a second
/// unique index on the hottest table is not worth it, since the row's <c>org_id</c> must equal the organization in force (row-level
/// security checks inserts) and the message is found through row-level security first, so a message of another organization is
/// never reachable. A delete keeps the message row (a tombstone), so <c>ON DELETE CASCADE</c> only serves a hard delete of an
/// organization's data; the delete statement erases the reactions itself.</item>
/// <item><c>messages.reaction_summary</c>: <c>[{ emoji, count, sample: [account ids, at most 3] }]</c> in the order of each emoji's
/// first reaction, null while nobody reacted. It is rebuilt from the rows by every change (under the conversation row lock), so
/// reading a page costs no aggregate: the summary comes with the page statement, and only <c>reactedByMe</c> needs one more
/// statement (skipped when no message of the page has a reaction). The array length is the number of distinct emoji, so the check
/// holds the limit of 50 at the database too, and a tombstone has none.</item>
/// </list>
/// Both are new and empty: no data step. The column is added without a default, so the table is not rewritten.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261128150000_AddReactions")]
internal sealed class AddReactions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            CREATE TABLE messaging.reactions (
                org_id uuid NOT NULL,
                message_id uuid NOT NULL,
                account_id uuid NOT NULL,
                emoji text NOT NULL CHECK (octet_length(emoji) BETWEEN 1 AND 32),
                created_at timestamptz NOT NULL,
                PRIMARY KEY (message_id, account_id, emoji),
                FOREIGN KEY (message_id) REFERENCES messaging.messages (id) ON DELETE CASCADE);

            ALTER TABLE messaging.messages ADD COLUMN reaction_summary jsonb;
            -- Cheap: the column is new and all NULL (a constraint on a large existing column needs NOT VALID, then VALIDATE in a separate migration).
            ALTER TABLE messaging.messages ADD CONSTRAINT messages_reaction_summary_check
                CHECK (reaction_summary IS NULL
                    OR (deleted_at IS NULL AND jsonb_typeof(reaction_summary) = 'array' AND jsonb_array_length(reaction_summary) BETWEEN 1 AND 50));
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(MessagingDbContext.Schema, "reactions");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            ALTER TABLE messaging.messages DROP CONSTRAINT messages_reaction_summary_check;
            ALTER TABLE messaging.messages DROP COLUMN reaction_summary;
            DROP TABLE messaging.reactions;
            """);
    }
}
