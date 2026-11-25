using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Infrastructure;

/// <summary>Files; row-level security limits every statement to the entered organization, and each query also names it.</summary>
internal sealed class FileStore(DatabaseSession session) : IFileStore
{
    private const string AddSql = """
        INSERT INTO files.files (id, org_id, uploader_account_id, conversation_id, name, size, declared_type, kind, storage_key, status, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'pending', $10)
        """;

    private const string FindSql = """
        SELECT id, uploader_account_id, conversation_id, name, size, declared_type, detected_type, kind, storage_key, status, created_at
        FROM files.files
        WHERE org_id = $1 AND id = $2
        """;

    private const string MarkScanningSql = """
        UPDATE files.files SET status = 'scanning', detected_type = $3, kind = $4
        WHERE org_id = $1 AND id = $2 AND status = 'pending'
        """;

    public async Task AddAsync(NewFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var command = new NpgsqlCommand(AddSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = file.Id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = file.OrgId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = file.UploaderAccountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = file.ConversationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = file.Name });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = file.Size });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = file.DeclaredType });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = FileKindNames.Of(file.Kind) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = file.StorageKey });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = file.CreatedAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FileRecord?> FindAsync(Guid organizationId, Guid fileId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = fileId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FileRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
            ParseKind(reader.GetString(7)),
            reader.GetString(8),
            ParseStatus(reader.GetString(9)),
            reader.GetFieldValue<DateTimeOffset>(10));
    }

    public async Task<bool> MarkScanningAsync(Guid organizationId, Guid fileId, string detectedType, FileKind kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarkScanningSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = fileId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = detectedType });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = FileKindNames.Of(kind) });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static FileKind ParseKind(string value) => value switch
    {
        "document" => FileKind.Document,
        "image" => FileKind.Image,
        "spreadsheet" => FileKind.Spreadsheet,
        "archive" => FileKind.Archive,
        "other" => FileKind.Other,
        _ => throw new InvalidOperationException("A stored file has an unknown kind."),
    };

    private static FileStatus ParseStatus(string value) => value switch
    {
        "pending" => FileStatus.Pending,
        "scanning" => FileStatus.Scanning,
        "ready" => FileStatus.Ready,
        "rejected" => FileStatus.Rejected,
        "attached" => FileStatus.Attached,
        "deleted" => FileStatus.Deleted,
        "expired" => FileStatus.Expired,
        _ => throw new InvalidOperationException("A stored file has an unknown status."),
    };
}
