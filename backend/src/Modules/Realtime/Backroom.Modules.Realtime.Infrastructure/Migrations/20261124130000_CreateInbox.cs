using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;

namespace Backroom.Modules.Realtime.Infrastructure.Migrations;

/// <summary>The inbox of the <c>organization.member-removed.v1</c> consumer, under row-level security. The module has no other tables.</summary>
[DbContext(typeof(RealtimeDbContext))]
[Migration("20261124130000_CreateInbox")]
internal sealed class CreateInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.CreateInboxTable(RealtimeDbContext.Schema);
    }
}
