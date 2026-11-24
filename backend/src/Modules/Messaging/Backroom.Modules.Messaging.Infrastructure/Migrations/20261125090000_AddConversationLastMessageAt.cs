using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 3 (docs/contracts/phase-2.md, "Data" and <c>GET /conversations</c>): <c>conversations.last_message_at</c>,
/// the time of the newest message, which orders a person's conversation list as <c>(last_message_at DESC, id DESC)</c>
/// (a conversation without messages sorts by <c>created_at</c>). A send sets it in the statement that takes the next
/// sequence number. No index: the list reads one person's open memberships (at most 10,000, served by
/// <c>conversation_members_account_idx</c>), joins their conversations by primary key and keeps the top rows of the
/// sort, so an index on <c>conversations</c> alone would not be used for the order.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261125090000_AddConversationLastMessageAt")]
internal sealed class AddConversationLastMessageAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '10min';

            ALTER TABLE messaging.conversations ADD COLUMN last_message_at timestamptz;

            -- Row-level security would hide every row from the migrator, which owns the tables.
            ALTER TABLE messaging.conversations NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE messaging.messages NO FORCE ROW LEVEL SECURITY;

            UPDATE messaging.conversations c
            SET last_message_at = newest.created_at
            FROM (SELECT conversation_id, max(created_at) AS created_at FROM messaging.messages GROUP BY conversation_id) AS newest
            WHERE c.id = newest.conversation_id;

            ALTER TABLE messaging.conversations FORCE ROW LEVEL SECURITY;
            ALTER TABLE messaging.messages FORCE ROW LEVEL SECURITY;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            ALTER TABLE messaging.conversations DROP COLUMN last_message_at;
            """);
    }
}
