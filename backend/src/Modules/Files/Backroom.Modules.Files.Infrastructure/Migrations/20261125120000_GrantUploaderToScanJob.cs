using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Files.Infrastructure.Migrations;

/// <summary>
/// The scan job <c>files.file-scan</c> tells the uploader when a scan rejects the upload (<c>FileRejected</c> live event,
/// docs/contracts/phase-2.md, "Live events"). The maintenance role gets <c>SELECT</c> of <c>uploader_account_id</c> only, to
/// address that notice (an id, never the name or the type); the other grants stay as <c>AddFileScanning</c> made them.
/// </summary>
[DbContext(typeof(FilesDbContext))]
[Migration("20261125120000_GrantUploaderToScanJob")]
internal sealed class GrantUploaderToScanJob : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.GrantMaintenanceColumns(FilesDbContext.Schema, "files", ["uploader_account_id"], []);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"REVOKE SELECT (uploader_account_id) ON {FilesDbContext.Schema}.files FROM {DatabaseRoles.Maintenance};");
    }
}
