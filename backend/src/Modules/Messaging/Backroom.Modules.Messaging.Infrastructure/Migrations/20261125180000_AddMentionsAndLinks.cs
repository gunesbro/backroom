using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 6 (docs/contracts/phase-2.md, flow 2 and "Data"): what the server derives from a message's blocks
/// besides its text.
/// <list type="bullet">
/// <item><c>message_mentions</c>: one row per mentioned account that could read the conversation when the message was
/// sent. Nobody else gets a row, so nothing built on this table can tell a person about a conversation they are not in.</item>
/// <item><c>message_links</c>: the <c>http</c> and <c>https</c> addresses a message links to, each once, in their order
/// in the message (<c>position</c>, from 1).</item>
/// </list>
/// Both tables are new and empty, so there is no data step: Phase 1 messages are plain text and have neither. The
/// foreign keys take a short lock on <c>messages</c>; the wait for it is bounded. Indexes for the read paths (unread
/// mention counts, the Links tab) come with those steps, when their queries exist.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261125180000_AddMentionsAndLinks")]
internal sealed class AddMentionsAndLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            CREATE TABLE messaging.message_mentions (
                org_id uuid NOT NULL,
                message_id uuid NOT NULL,
                conversation_id uuid NOT NULL,
                account_id uuid NOT NULL,
                -- A person is mentioned by a message once, however many nodes name them; also the erase path of a delete.
                PRIMARY KEY (message_id, account_id),
                FOREIGN KEY (message_id) REFERENCES messaging.messages (id) ON DELETE CASCADE);

            CREATE TABLE messaging.message_links (
                org_id uuid NOT NULL,
                conversation_id uuid NOT NULL,
                message_id uuid NOT NULL,
                -- The link's place among the message's distinct links, from 1.
                position smallint NOT NULL CHECK (position > 0),
                -- As the link mark holds it (MessageBlocks.MaxHrefLength, ASCII).
                url text NOT NULL CHECK (char_length(url) BETWEEN 1 AND 2048),
                -- Lower-case ASCII (punycode for international names).
                host text NOT NULL CHECK (char_length(host) BETWEEN 1 AND 253),
                created_at timestamptz NOT NULL,
                PRIMARY KEY (message_id, position),
                FOREIGN KEY (message_id) REFERENCES messaging.messages (id) ON DELETE CASCADE);
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(MessagingDbContext.Schema, "message_mentions");
        migrationBuilder.EnableOrganizationRowLevelSecurity(MessagingDbContext.Schema, "message_links");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // The messages keep their blocks; only the derived rows go.
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            DROP TABLE messaging.message_links;
            DROP TABLE messaging.message_mentions;
            """);
    }
}
