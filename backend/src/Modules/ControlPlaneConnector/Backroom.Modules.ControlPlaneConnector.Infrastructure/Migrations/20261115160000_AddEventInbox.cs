using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Migrations;

/// <summary>
/// The connector consumes integration events (invitations now, memberships later), so it needs the standard
/// inbox table every consuming module has, under its standard name <c>inbox</c> (organization-scoped, with
/// row-level security). The link's own inbox of downstream message ids becomes <c>link_inbox</c>; its grants
/// stay with the table.
/// </summary>
[DbContext(typeof(ControlPlaneConnectorDbContext))]
[Migration("20261115160000_AddEventInbox")]
internal sealed class AddEventInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE control_plane_connector.inbox RENAME TO link_inbox;
            ALTER INDEX control_plane_connector.inbox_pkey RENAME TO link_inbox_pkey;
            """);
        migrationBuilder.CreateInboxTable(ControlPlaneConnectorDbContext.Schema);
    }
}
