using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 5 (docs/contracts/phase-2.md, "Sequence numbers" and "Data"; docs/adr/0011, "One commit-ordered change
/// counter per conversation"; docs/adr/0012 keeps how numbers are assigned).
/// <list type="bullet">
/// <item><c>conversations.last_seq</c> becomes <c>change_seq</c> (every change takes the next number) and
/// <c>last_message_seq</c> (the <c>seq</c> of the newest message) is added. Phase 1 only had sends, so both start at
/// the old <c>last_seq</c>, except where a lost idempotent race left a gap at the end; hence the backfill from the
/// messages.</item>
/// <item><c>messages.version_seq</c>, the number of the message's latest change: its <c>seq</c> until it is edited,
/// deleted or otherwise changed. Unique index <c>(conversation_id, version_seq)</c> serves <c>changed_after</c>.</item>
/// <item>Tombstones: a deleted message keeps its row (<c>id</c>, <c>seq</c>, <c>version_seq</c>, <c>deleted_at</c>)
/// and loses its content, so <c>text</c> and <c>blocks</c> may be null exactly when <c>deleted_at</c> is set.</item>
/// </list>
/// The backfills are one statement per table: the migration is one transaction and the data helper holds the tables'
/// <c>ACCESS EXCLUSIVE</c> lock until it commits, so smaller batches would not shorten the stall, only add passes over
/// the rows already rewritten. Lock waits give up after 5 seconds and the whole migration after 10 minutes.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261125120000_AddChangeSequences")]
internal sealed class AddChangeSequences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '10min';

            ALTER TABLE messaging.conversations RENAME COLUMN last_seq TO change_seq;
            ALTER TABLE messaging.conversations ADD COLUMN last_message_seq bigint NOT NULL DEFAULT 0;

            ALTER TABLE messaging.messages ADD COLUMN version_seq bigint;
            ALTER TABLE messaging.messages ALTER COLUMN text DROP NOT NULL;
            ALTER TABLE messaging.messages ALTER COLUMN blocks DROP NOT NULL;
            """);

        // Row-level security is forced on the owner too, so the backfills go through OrganizationDataSql.
        migrationBuilder.OrganizationDataSql(MessagingDbContext.Schema, ["conversations", "messages"], """
            UPDATE messaging.messages SET version_seq = seq;

            UPDATE messaging.conversations c
            SET last_message_seq = newest.seq
            FROM (SELECT conversation_id, max(seq) AS seq FROM messaging.messages GROUP BY conversation_id) AS newest
            WHERE c.id = newest.conversation_id;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE messaging.messages ALTER COLUMN version_seq SET NOT NULL;
            ALTER TABLE messaging.messages ADD CONSTRAINT messages_version_seq_check CHECK (version_seq >= seq);
            -- A tombstone has no content; every other message has both forms of it.
            ALTER TABLE messaging.messages ADD CONSTRAINT messages_tombstone_check
                CHECK ((deleted_at IS NULL AND text IS NOT NULL AND blocks IS NOT NULL) OR (deleted_at IS NOT NULL AND text IS NULL AND blocks IS NULL));
            ALTER TABLE messaging.conversations ADD CONSTRAINT conversations_last_message_seq_check
                CHECK (last_message_seq >= 0 AND last_message_seq <= change_seq);

            -- Catch-up (changed_after): the messages of a conversation in the order of their latest change. Unique: a
            -- change number belongs to one message, which makes version_seq a complete keyset.
            CREATE UNIQUE INDEX messages_conversation_version_seq_idx ON messaging.messages (conversation_id, version_seq);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Tombstones and changed messages cannot be expressed in the earlier schema: refuse rather than lose them.
        migrationBuilder.OrganizationDataSql(MessagingDbContext.Schema, ["messages"], """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM messaging.messages WHERE deleted_at IS NOT NULL OR version_seq <> seq) THEN
                    RAISE EXCEPTION 'Deleted or changed messages exist; the change sequences cannot be removed.';
                END IF;
            END $$;
            """);

        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '10min';

            DROP INDEX messaging.messages_conversation_version_seq_idx;
            ALTER TABLE messaging.conversations DROP CONSTRAINT conversations_last_message_seq_check;
            ALTER TABLE messaging.messages DROP CONSTRAINT messages_tombstone_check;
            ALTER TABLE messaging.messages DROP CONSTRAINT messages_version_seq_check;
            ALTER TABLE messaging.messages ALTER COLUMN blocks SET NOT NULL;
            ALTER TABLE messaging.messages ALTER COLUMN text SET NOT NULL;
            ALTER TABLE messaging.messages DROP COLUMN version_seq;
            ALTER TABLE messaging.conversations DROP COLUMN last_message_seq;
            ALTER TABLE messaging.conversations RENAME COLUMN change_seq TO last_seq;
            """);
    }
}
