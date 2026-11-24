using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Application.Storage;

namespace Backroom.Modules.Files.Infrastructure.Scanning;

/// <summary>
/// Scans completed uploads for malware (docs/contracts/phase-2.md, flow 6, step 3; docs/adr/0010): the oldest file in
/// <c>scanning</c> goes to <see cref="IFileScanner"/>, streamed from storage, and becomes <c>ready</c> when clean or
/// <c>rejected</c> (object deleted) when infected. One file per batch, because the row stays locked and the
/// maintenance connection stays open during the scan (<see cref="ClamAvOptions.ScanTimeout"/> keeps it under the
/// role's idle-in-transaction timeout). The role reads no name or type, only ids, the key, the size and times.
/// An unavailable scanner throws: the transaction rolls back, the file stays <c>scanning</c> and unserved, the runner
/// counts the failed run (the alert) and tries again at the next interval. It never fails open. Logs carry ids only.
/// </summary>
internal sealed partial class FileScanJob(
    IFileStorageProvider storageProvider,
    IFileScanner scanner,
    TimeProvider timeProvider,
    ILogger<FileScanJob> logger) : IMaintenanceJob, IMaintenanceBacklog
{
    public const string JobName = "files.file-scan";

    // By files_scanning_idx. Skips rows another worker is scanning.
    private const string NextSql = """
        SELECT id, org_id, storage_key, size
        FROM files.files
        WHERE status = 'scanning'
        ORDER BY created_at
        LIMIT 1
        FOR UPDATE SKIP LOCKED
        """;

    private const string ReadySql = "UPDATE files.files SET status = 'ready', ready_at = $2 WHERE id = $1 AND status = 'scanning'";

    private const string RejectSql = "UPDATE files.files SET status = 'rejected' WHERE id = $1 AND status = 'scanning'";

    // $1: the bound.
    private const string BacklogSql = """
        SELECT count(*)::integer, min(created_at)
        FROM (SELECT created_at FROM files.files WHERE status = 'scanning' ORDER BY created_at LIMIT $1) due
        """;

    public string Name => JobName;

    public TimeSpan Interval => TimeSpan.FromSeconds(10);

    public int BatchSize => 1;

    public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Guid id;
        Guid organizationId;
        string key;
        long size;
        await using (var next = new NpgsqlCommand(NextSql, connection, transaction))
        await using (var reader = await next.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return 0;
            }

            id = reader.GetGuid(0);
            organizationId = reader.GetGuid(1);
            key = reader.GetString(2);
            size = reader.GetInt64(3);
        }

        var storage = storageProvider.ForOrganization(organizationId);
        ScanVerdict verdict;
        try
        {
            verdict = await storage.ReadAsync(key, stream => scanner.ScanAsync(stream, size, cancellationToken), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Nothing to scan and nothing to serve: the file can never be used.
            verdict = ScanVerdict.Infected;
            LogMissing(logger, id, organizationId);
        }
        catch (ScannerUnavailableException)
        {
            LogUnavailable(logger, id, organizationId);
            throw;
        }

        if (verdict == ScanVerdict.Clean)
        {
            await MarkReadyAsync(connection, transaction, id, timeProvider.GetUtcNow(), cancellationToken);
            LogClean(logger, id, organizationId);
            return 1;
        }

        // The object goes first: if the update fails the row stays scanning and the next run finds no object (rejected).
        await storage.DeleteAsync(key, cancellationToken);
        await MarkRejectedAsync(connection, transaction, id, cancellationToken);
        LogRejected(logger, id, organizationId);
        return 1;
    }

    public async Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(BacklogSql, connection);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = MaintenanceBacklog.MaxCount });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oldest = await reader.IsDBNullAsync(1, cancellationToken) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        return new MaintenanceBacklog(reader.GetInt32(0), oldest);
    }

    private static async Task MarkReadyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTimeOffset readyAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReadySql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = readyAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkRejectedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RejectSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "File {FileId} in organization {OrganizationId} passed the malware scan.")]
    private static partial void LogClean(ILogger logger, Guid fileId, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "File {FileId} in organization {OrganizationId} was rejected by the malware scan; its object was deleted.")]
    private static partial void LogRejected(ILogger logger, Guid fileId, Guid organizationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The scanner is unavailable; file {FileId} in organization {OrganizationId} stays unscanned and unserved.")]
    private static partial void LogUnavailable(ILogger logger, Guid fileId, Guid organizationId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "The object of scanning file {FileId} in organization {OrganizationId} is missing; the file is rejected.")]
    private static partial void LogMissing(ILogger logger, Guid fileId, Guid organizationId);
}
