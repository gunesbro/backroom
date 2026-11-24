using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;

namespace Backroom.Modules.Files.Infrastructure.Migrations;

/// <summary>
/// The malware scan job <c>files.file-scan</c> (docs/contracts/phase-2.md, flow 6; docs/adr/0010).
/// <list type="bullet">
/// <item>A partial index on the files that wait for a scan, oldest first.</item>
/// <item>Grants for the maintenance role, columns only: <c>SELECT</c> of <c>id</c>, <c>org_id</c>, <c>status</c>,
/// <c>storage_key</c>, <c>size</c>, <c>created_at</c>, <c>scan_attempts</c> and <c>scan_next_at</c> (never the name, the types or the uploader), <c>UPDATE</c> of
/// <c>status</c>, <c>ready_at</c>, <c>scan_attempts</c> and <c>scan_next_at</c>. The job moves a scanning file to ready or rejected and deletes the object of an
/// infected one; it deletes no rows, so no cascade is reachable.</item>
/// </list>
/// </summary>
[DbContext(typeof(FilesDbContext))]
[Migration("20261125020000_AddFileScanning")]
internal sealed class AddFileScanning : Migration
{
    private static readonly string[] s_selectColumns = ["id", "org_id", "status", "storage_key", "size", "created_at", "scan_attempts", "scan_next_at"];
    private static readonly string[] s_updateColumns = ["status", "ready_at", "scan_attempts", "scan_next_at"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            -- Attempts made and when the next may start (-infinity: due now). A claim moves the time ahead, so a file the
            -- scanner cannot answer for never blocks the files behind it.
            ALTER TABLE files.files
                ADD COLUMN scan_attempts integer NOT NULL DEFAULT 0 CHECK (scan_attempts >= 0),
                ADD COLUMN scan_next_at timestamptz NOT NULL DEFAULT '-infinity';
            CREATE INDEX files_scanning_idx ON files.files (scan_next_at) WHERE status = 'scanning';
            """);
        migrationBuilder.GrantMaintenanceColumns(FilesDbContext.Schema, "files", s_selectColumns, s_updateColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.RevokeMaintenanceGrants(FilesDbContext.Schema, "files");
        migrationBuilder.Sql("""
            DROP INDEX files.files_scanning_idx;
            ALTER TABLE files.files DROP COLUMN scan_attempts, DROP COLUMN scan_next_at;
            """);
    }
}
