using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 1 (docs/contracts/phase-2.md, flow 1 and "Data"): Phase 1's channels become conversations in place.
/// <list type="bullet">
/// <item><c>channels</c> becomes <c>conversations</c> with <c>kind</c> (every existing row is a <c>public_channel</c>)
/// and <c>member_count</c>; <c>messages.channel_id</c> becomes <c>conversation_id</c>. Ids, names, sequence numbers
/// and the row-level security policy (it belongs to the table) are kept. <c>last_seq</c> and how it is assigned are
/// unchanged (Phase 1 decision 12; ADR 0012 owns the sequence numbers). Only channel kinds have a name.</item>
/// <item>Name uniqueness covers open <b>public</b> channels only, so neither creating nor renaming a channel can
/// reveal that a private channel of the same name exists (flow 1, "Channel names").</item>
/// <item><c>conversation_members</c>, under row-level security. Phase 1 had no membership, so each channel's creator
/// and everyone who posted in it become members, joined at their first message (the creator at the channel's
/// creation) with <c>joined_seq</c> at the channel's current <c>last_seq</c>, so their past history is not new to
/// them.</item>
/// </list>
/// The data steps run as the migrator, which owns the tables; row-level security is forced on the owner too, so
/// they go through <c>OrganizationDataSql</c>, which lifts <c>FORCE</c> inside the migration's transaction (the
/// <c>ALTER TABLE</c> lock keeps every other session out until commit) and restores it. <see cref="Down"/> refuses to run while any conversation other
/// than a public channel exists, because Phase 1's schema would make it public.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261124150000_RenameChannelsToConversations")]
internal sealed class RenameChannelsToConversations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            -- The renames and the backfill hold exclusive locks on conversations and messages until commit: give up
            -- instead of queueing every send behind a long wait, and bound each statement (the backfill scans messages).
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '10min';

            ALTER TABLE messaging.channels RENAME TO conversations;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT channels_pkey TO conversations_pkey;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT channels_org_id_id_key TO conversations_org_id_id_key;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT channels_name_check TO conversations_name_check;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT channels_name_key_check TO conversations_name_key_check;

            -- Phase 1 rows are all public channels; the default fills them, then every new row names its kind.
            ALTER TABLE messaging.conversations
                ADD COLUMN kind text NOT NULL DEFAULT 'public_channel'
                    CONSTRAINT conversations_kind_check CHECK (kind IN ('public_channel', 'private_channel', 'dm', 'group_dm', 'thread')),
                -- Open memberships; changed in the transaction that opens or closes one.
                ADD COLUMN member_count integer NOT NULL DEFAULT 0 CONSTRAINT conversations_member_count_check CHECK (member_count >= 0);
            ALTER TABLE messaging.conversations ALTER COLUMN kind DROP DEFAULT;

            -- Channels have a name; DMs, group DMs and threads have none (they are shown by participants or their root).
            ALTER TABLE messaging.conversations ALTER COLUMN name DROP NOT NULL, ALTER COLUMN name_key DROP NOT NULL;
            ALTER TABLE messaging.conversations ADD CONSTRAINT conversations_name_by_kind_check CHECK (
                CASE WHEN kind IN ('public_channel', 'private_channel')
                    THEN name IS NOT NULL AND name_key IS NOT NULL
                    ELSE name IS NULL AND name_key IS NULL
                END);

            -- One open public channel per name; private channels are not unique and are never compared.
            DROP INDEX messaging.channels_name_key_idx;
            CREATE UNIQUE INDEX conversations_public_name_key_idx ON messaging.conversations (org_id, name_key)
                WHERE kind = 'public_channel' AND archived_at IS NULL;

            -- Browsing open public channels (keyset by id).
            DROP INDEX messaging.channels_open_idx;
            CREATE INDEX conversations_public_open_idx ON messaging.conversations (org_id, id)
                WHERE kind = 'public_channel' AND archived_at IS NULL;

            ALTER TABLE messaging.messages RENAME COLUMN channel_id TO conversation_id;
            ALTER TABLE messaging.messages RENAME CONSTRAINT messages_channel_seq_key TO messages_conversation_seq_key;
            ALTER TABLE messaging.messages RENAME CONSTRAINT messages_org_id_channel_id_fkey TO messages_org_id_conversation_id_fkey;

            CREATE TABLE messaging.conversation_members (
                org_id uuid NOT NULL,
                conversation_id uuid NOT NULL,
                account_id uuid NOT NULL,
                role text NOT NULL CHECK (role IN ('owner', 'member')),
                joined_at timestamptz NOT NULL,
                -- The conversation's last sequence number when this membership was (re)opened.
                joined_seq bigint NOT NULL CHECK (joined_seq >= 0),
                added_by uuid NOT NULL,
                -- Set when the person leaves or is removed; a later join reopens the row.
                left_at timestamptz,
                -- Also the read-access probe: is this account an open member of this conversation.
                PRIMARY KEY (conversation_id, account_id),
                FOREIGN KEY (org_id, conversation_id) REFERENCES messaging.conversations (org_id, id));

            -- A person's open conversations (the sidebar, the membership limit).
            CREATE INDEX conversation_members_account_idx ON messaging.conversation_members (org_id, account_id, conversation_id)
                WHERE left_at IS NULL;
            """);

        // Existing members: the creator and every author. Row-level security is forced on the owner too, so the data
        // steps run through OrganizationDataSql, which lifts FORCE on the tables they touch and restores it.
        migrationBuilder.OrganizationDataSql(MessagingDbContext.Schema, ["conversations", "messages"], """
            INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by)
            SELECT participant.org_id, participant.conversation_id, participant.account_id, 'member', min(participant.joined_at), c.last_seq, participant.account_id
            FROM (
                SELECT org_id, id AS conversation_id, created_by AS account_id, created_at AS joined_at FROM messaging.conversations
                UNION ALL
                SELECT org_id, conversation_id, author_account_id, created_at FROM messaging.messages
            ) AS participant
            JOIN messaging.conversations c ON c.id = participant.conversation_id
            GROUP BY participant.org_id, participant.conversation_id, participant.account_id, c.last_seq;

            UPDATE messaging.conversations c
            SET member_count = counted.members
            FROM (SELECT conversation_id, count(*) AS members FROM messaging.conversation_members GROUP BY conversation_id) AS counted
            WHERE c.id = counted.conversation_id;
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(MessagingDbContext.Schema, "conversation_members");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '10min';

            ALTER TABLE messaging.conversations NO FORCE ROW LEVEL SECURITY;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM messaging.conversations WHERE kind <> 'public_channel') THEN
                    RAISE EXCEPTION 'Conversations other than public channels exist; Phase 1''s schema would make them public.';
                END IF;
            END
            $$;
            ALTER TABLE messaging.conversations FORCE ROW LEVEL SECURITY;

            DROP TABLE messaging.conversation_members;

            ALTER TABLE messaging.messages RENAME CONSTRAINT messages_org_id_conversation_id_fkey TO messages_org_id_channel_id_fkey;
            ALTER TABLE messaging.messages RENAME CONSTRAINT messages_conversation_seq_key TO messages_channel_seq_key;
            ALTER TABLE messaging.messages RENAME COLUMN conversation_id TO channel_id;

            DROP INDEX messaging.conversations_public_open_idx;
            DROP INDEX messaging.conversations_public_name_key_idx;
            ALTER TABLE messaging.conversations DROP CONSTRAINT conversations_name_by_kind_check;
            ALTER TABLE messaging.conversations DROP COLUMN member_count, DROP COLUMN kind;
            ALTER TABLE messaging.conversations ALTER COLUMN name SET NOT NULL, ALTER COLUMN name_key SET NOT NULL;
            CREATE UNIQUE INDEX channels_name_key_idx ON messaging.conversations (org_id, name_key) WHERE archived_at IS NULL;
            CREATE INDEX channels_open_idx ON messaging.conversations (org_id, id) WHERE archived_at IS NULL;

            ALTER TABLE messaging.conversations RENAME CONSTRAINT conversations_name_key_check TO channels_name_key_check;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT conversations_name_check TO channels_name_check;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT conversations_org_id_id_key TO channels_org_id_id_key;
            ALTER TABLE messaging.conversations RENAME CONSTRAINT conversations_pkey TO channels_pkey;
            ALTER TABLE messaging.conversations RENAME TO channels;
            """);
    }
}
