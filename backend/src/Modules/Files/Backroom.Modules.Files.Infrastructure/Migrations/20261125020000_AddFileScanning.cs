using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;

namespace Backroom.Modules.Files.Infrastructure.Migrations;

/// <summary>
/// The malware scan job <c>files.file-scan</c> (docs/contracts/phase-2.md, flow 6; docs/adr/0010).
/// <list type="bullet">
/// <item>A partial index on the files that wait for a scan, oldest first.</item>
/// <item>Grants for the maintenance role, columns only: <c>SELECT</c> of <c>id</c>, <c>org_id</c>, <c>status</c>,
/// <c>storage_key</c>, <c>size</c> and <c>created_at</c> (never the name, the types or the uploader), <c>UPDATE</c> of
/// <c>status</c> and <c>ready_at</c>. The job moves a scanning file to ready or rejected and deletes the object of an
/// infected one; it deletes no rows, so no cascade is reachable.</item>
/// </list>
/// </summary>
[DbContext(typeof(FilesDbContext))]
[Migration("20261125020000_AddFileScanning")]
internal sealed class AddFileScanning : Migration
{
    private static readonly string[] s_selectColumns = ["id", "org_id", "status", "storage_key", "size", "created_at"];
    private static readonly string[] s_updateColumns = ["status", "ready_at"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE INDEX files_scanning_idx ON files.files (created_at) WHERE status = 'scanning';
            """);
        migrationBuilder.GrantMaintenanceColumns(FilesDbContext.Schema, "files", s_selectColumns, s_updateColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.RevokeMaintenanceGrants(FilesDbContext.Schema, "files");
        migrationBuilder.Sql("""
            DROP INDEX files.files_scanning_idx;
            """);
    }
}
