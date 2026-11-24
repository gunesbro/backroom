using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Files.Infrastructure.Migrations;

/// <summary>
/// Uploaded files and the module's outbox (docs/contracts/phase-2.md, "Data"), each under row-level security. The
/// quota's <c>org_usage</c> and <c>reserved_bytes</c> (step 24), thumbnails (step 25) and deletion (step 22) add
/// their columns and tables with their steps.
/// </summary>
[DbContext(typeof(FilesDbContext))]
[Migration("20261124150000_CreateFiles")]
internal sealed class CreateFiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE files.files (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                uploader_account_id uuid NOT NULL,
                -- The conversation the file was uploaded to; its access decides who may use the file.
                conversation_id uuid NOT NULL,
                -- For display only (FileName: NFC, 1 to 255 code points); never in storage keys, URLs, logs or events.
                name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 255),
                -- Declared at the start, checked against the stored object at completion; 100 MB at most.
                size bigint NOT NULL CHECK (size BETWEEN 1 AND 104857600),
                declared_type text NOT NULL CHECK (char_length(declared_type) BETWEEN 1 AND 128),
                -- Confirmed from the object's first bytes at completion.
                detected_type text CHECK (char_length(detected_type) BETWEEN 1 AND 128),
                kind text NOT NULL CHECK (kind IN ('document', 'image', 'spreadsheet', 'archive', 'other')),
                -- {org_id}/{id}, chosen by the server.
                storage_key text NOT NULL CHECK (char_length(storage_key) BETWEEN 1 AND 200),
                status text NOT NULL CHECK (status IN ('pending', 'scanning', 'ready', 'rejected', 'attached', 'deleted', 'expired')),
                created_at timestamptz NOT NULL,
                ready_at timestamptz,
                -- A usable file has a confirmed type.
                CONSTRAINT files_confirmed_type_check CHECK (status NOT IN ('ready', 'attached') OR (detected_type IS NOT NULL AND ready_at IS NOT NULL)),
                -- Target of the composite foreign keys of later tables (thumbnails).
                CONSTRAINT files_org_id_id_key UNIQUE (org_id, id),
                CONSTRAINT files_storage_key_key UNIQUE (storage_key));
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(FilesDbContext.Schema, "files");
        migrationBuilder.CreateOutboxTable(FilesDbContext.Schema);
    }
}
