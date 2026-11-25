using System.Collections.Concurrent;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Application.Storage;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Files.Infrastructure.Scanning;

/// <summary>
/// Scans completed uploads for malware (docs/contracts/phase-2.md, flow 6, step 3; docs/adr/0010). No database
/// connection is held while a file is scanned: each run of the job (as the maintenance role, in one short transaction)
/// (1) applies the outcomes of scans that finished since the last run, with updates that only touch rows still in
/// <c>scanning</c>, and (2) claims the files that are due: <c>scan_attempts</c> goes up and <c>scan_next_at</c> moves
/// ahead by an exponential backoff (5 minutes, doubling), so a file the scanner cannot answer for never blocks the
/// files behind it, and another worker does not take it meanwhile. The claimed files are scanned in the background, at
/// most <see cref="Concurrency"/> at a time, streamed from storage to <see cref="IFileScanner"/>; their outcomes wait
/// in memory for the next run (a lost outcome only means a retry after the backoff).
/// A rejection also tells the uploader (<c>FileRejected</c>, ids and a reason code only): the notice is staged when the update
/// that rejects the file ran, and published at the start of the next run, so it never goes out before that transaction
/// committed (a lost notice only means the uploader finds the file rejected when looking).
/// A clean file becomes <c>ready</c>. An infected one, one whose object is gone, and one that failed
/// <see cref="MaxAttempts"/> times become <c>rejected</c> and their object is deleted. When the scanner itself cannot
/// be reached the attempt is given back and claiming pauses for <see cref="OutagePause"/>: an outage never rejects
/// files, and a file stays <c>scanning</c> and unserved until a scan answers. The role reads no name or type. Logs carry
/// ids only.
/// </summary>
internal sealed partial class FileScanJob : IMaintenanceJob, IMaintenanceBacklog, IDisposable
{
    public const string JobName = "files.file-scan";

    /// <summary>Scans at once; the maintenance pool's two connections are not involved.</summary>
    public const int Concurrency = 4;

    /// <summary>Attempts a file gets before it is rejected.</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long claiming stops after the scanner could not be reached.</summary>
    public static readonly TimeSpan OutagePause = TimeSpan.FromSeconds(30);

    private const int ClaimLimit = 16;

    // Claims due rows (by files_scanning_idx), skipping rows another worker holds: $1 now, $2 limit. The backoff is 5
    // minutes doubled for every earlier attempt (at most 2^6), which also exceeds the scan timeout, so a running scan
    // is never claimed again.
    private const string ClaimSql = """
        WITH due AS MATERIALIZED (
            SELECT id FROM files.files
            WHERE status = 'scanning' AND scan_next_at <= $1
            ORDER BY scan_next_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        UPDATE files.files f
        SET scan_attempts = f.scan_attempts + 1,
            scan_next_at = $1 + make_interval(secs => 300 * power(2, LEAST(f.scan_attempts, 6)))
        FROM due
        WHERE f.id = due.id
        RETURNING f.id, f.org_id, f.storage_key, f.size, f.scan_attempts
        """;

    private const string ReadySql = "UPDATE files.files SET status = 'ready', ready_at = $2 WHERE id = $1 AND status = 'scanning'";

    private const string RejectSql = "UPDATE files.files SET status = 'rejected' WHERE id = $1 AND status = 'scanning' RETURNING org_id, uploader_account_id";

    private const string RefundSql = "UPDATE files.files SET scan_attempts = GREATEST(scan_attempts - 1, 0), scan_next_at = $2 WHERE id = $1 AND status = 'scanning'";

    // $1: the bound, $2: now.
    private const string BacklogSql = """
        SELECT count(*)::integer, min(created_at)
        FROM (SELECT created_at FROM files.files WHERE status = 'scanning' AND scan_next_at <= $2 ORDER BY scan_next_at LIMIT $1) due
        """;

    private readonly IFileStorageProvider _storageProvider;
    private readonly IFileScanner _scanner;
    private readonly ILiveEventPublisher _live;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileScanJob> _logger;
    private readonly SemaphoreSlim _slots = new(Concurrency, Concurrency);
    private readonly ConcurrentQueue<Outcome> _outcomes = new();
    private readonly ConcurrentQueue<FileRejectedPayload> _staged = new();
    private readonly CancellationTokenSource _stop = new();
    private int _inFlight;
    private long _pausedUntilTicks;

    public FileScanJob(IFileStorageProvider storageProvider, IFileScanner scanner, ILiveEventPublisher live, TimeProvider timeProvider, ILogger<FileScanJob> logger)
    {
        _storageProvider = storageProvider;
        _scanner = scanner;
        _live = live;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private enum OutcomeKind
    {
        /// <summary>No change: the claim's backoff decides when the file is tried again.</summary>
        Failed,
        Ready,
        Rejected,

        /// <summary>The scanner could not be reached: the attempt is given back.</summary>
        Outage,
    }

    /// <summary>Scans running or finished but not yet applied (for tests that run the job until it is idle).</summary>
    internal int PendingWork => Volatile.Read(ref _inFlight) + _outcomes.Count + _staged.Count;

    public string Name => JobName;

    public TimeSpan Interval => TimeSpan.FromSeconds(10);

    public int BatchSize => ClaimLimit;

    public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // The notices staged by earlier runs, whose transactions the runner committed before it started this one.
        var due = new List<FileRejectedPayload>();
        while (_staged.TryDequeue(out var notice))
        {
            due.Add(notice);
        }

        foreach (var notice in due)
        {
            await _live.PublishAsync(notice, cancellationToken);
        }

        var now = _timeProvider.GetUtcNow();
        var applied = new List<Outcome>();
        var rejected = new List<FileRejectedPayload>();
        while (applied.Count < 64 && _outcomes.TryDequeue(out var outcome))
        {
            applied.Add(outcome);
        }

        try
        {
            foreach (var outcome in applied)
            {
                await ApplyAsync(connection, transaction, outcome, now, rejected, cancellationToken);
            }
        }
        catch
        {
            // The runner rolls back; the outcomes are applied again at the next run (the updates are conditional).
            foreach (var outcome in applied)
            {
                _outcomes.Enqueue(outcome);
            }

            throw;
        }

        foreach (var notice in rejected)
        {
            _staged.Enqueue(notice);
        }

        var claims = new List<Claim>();
        var capacity = ClaimLimit - Volatile.Read(ref _inFlight);
        if (capacity > 0 && now.UtcTicks >= Interlocked.Read(ref _pausedUntilTicks))
        {
            await using var command = new NpgsqlCommand(ClaimSql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = capacity });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claims.Add(new Claim(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4)));
            }
        }

        foreach (var claim in claims)
        {
            Interlocked.Increment(ref _inFlight);
            _ = Task.Run(() => ScanInBackgroundAsync(claim), CancellationToken.None);
        }

        return Math.Min(applied.Count + claims.Count, BatchSize);
    }

    public async Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(BacklogSql, connection);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = MaintenanceBacklog.MaxCount });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = _timeProvider.GetUtcNow() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oldest = await reader.IsDBNullAsync(1, cancellationToken) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        return new MaintenanceBacklog(reader.GetInt32(0), oldest);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _slots.Dispose();
    }

    private static async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Outcome outcome, DateTimeOffset now, List<FileRejectedPayload> rejected, CancellationToken cancellationToken)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ready:
                await using (var command = new NpgsqlCommand(ReadySql, connection, transaction))
                {
                    command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = outcome.Id });
                    command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                break;
            case OutcomeKind.Rejected:
                await using (var command = new NpgsqlCommand(RejectSql, connection, transaction))
                {
                    command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = outcome.Id });
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        rejected.Add(new FileRejectedPayload(reader.GetGuid(0), reader.GetGuid(1), outcome.Id, outcome.Reason ?? FileRejectedPayload.ScanFailed));
                    }
                }

                break;
            case OutcomeKind.Outage:
                await using (var command = new NpgsqlCommand(RefundSql, connection, transaction))
                {
                    command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = outcome.Id });
                    command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now + OutagePause });
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                break;
            default:
                break;
        }
    }

    private async Task ScanInBackgroundAsync(Claim claim)
    {
        Outcome outcome;
        try
        {
            await _slots.WaitAsync(_stop.Token);
            try
            {
                outcome = await ScanAsync(claim, _stop.Token);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _inFlight);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogFailed(_logger, claim.Id, claim.OrganizationId, exception.GetType().Name);
            outcome = new Outcome(claim.Id, OutcomeKind.Failed);
        }

        _outcomes.Enqueue(outcome);
        Interlocked.Decrement(ref _inFlight);
    }

    private async Task<Outcome> ScanAsync(Claim claim, CancellationToken cancellationToken)
    {
        var storage = _storageProvider.ForOrganization(claim.OrganizationId);
        if (claim.Attempt > MaxAttempts)
        {
            // Never answered in the allowed attempts: nothing is served from it.
            await storage.DeleteAsync(claim.StorageKey, cancellationToken);
            LogGaveUp(_logger, claim.Id, claim.OrganizationId, MaxAttempts);
            return new Outcome(claim.Id, OutcomeKind.Rejected, FileRejectedPayload.ScanFailed);
        }

        ScanVerdict verdict;
        try
        {
            verdict = await storage.ReadAsync(claim.StorageKey, stream => _scanner.ScanAsync(stream, claim.Size, cancellationToken), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Nothing to scan and nothing to serve: the file can never be used.
            LogMissing(_logger, claim.Id, claim.OrganizationId);
            return new Outcome(claim.Id, OutcomeKind.Rejected, FileRejectedPayload.ScanFailed);
        }
        catch (ScannerUnavailableException exception)
        {
            LogUnavailable(_logger, claim.Id, claim.OrganizationId, exception.IsOutage);
            if (exception.IsOutage)
            {
                Interlocked.Exchange(ref _pausedUntilTicks, (_timeProvider.GetUtcNow() + OutagePause).UtcTicks);
                return new Outcome(claim.Id, OutcomeKind.Outage);
            }

            return new Outcome(claim.Id, OutcomeKind.Failed);
        }

        if (verdict == ScanVerdict.Clean)
        {
            LogClean(_logger, claim.Id, claim.OrganizationId);
            return new Outcome(claim.Id, OutcomeKind.Ready);
        }

        // The object goes first: if this fails the file stays scanning and is tried again.
        await storage.DeleteAsync(claim.StorageKey, cancellationToken);
        LogRejected(_logger, claim.Id, claim.OrganizationId);
        return new Outcome(claim.Id, OutcomeKind.Rejected, FileRejectedPayload.Malware);
    }

    private sealed record Claim(Guid Id, Guid OrganizationId, string StorageKey, long Size, int Attempt);

    private sealed record Outcome(Guid Id, OutcomeKind Kind, string? Reason = null);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "File {FileId} in organization {OrganizationId} passed the malware scan.")]
    private static partial void LogClean(ILogger logger, Guid fileId, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "File {FileId} in organization {OrganizationId} was rejected by the malware scan; its object was deleted.")]
    private static partial void LogRejected(ILogger logger, Guid fileId, Guid organizationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The scanner gave no answer for file {FileId} in organization {OrganizationId} (unreachable: {Outage}); it stays unscanned and unserved.")]
    private static partial void LogUnavailable(ILogger logger, Guid fileId, Guid organizationId, bool outage);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "The object of scanning file {FileId} in organization {OrganizationId} is missing; the file is rejected.")]
    private static partial void LogMissing(ILogger logger, Guid fileId, Guid organizationId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "File {FileId} in organization {OrganizationId} got no scan answer in {MaxAttempts} attempts; it is rejected and its object deleted.")]
    private static partial void LogGaveUp(ILogger logger, Guid fileId, Guid organizationId, int maxAttempts);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Scanning file {FileId} in organization {OrganizationId} failed ({ExceptionType}); it is tried again after its backoff.")]
    private static partial void LogFailed(ILogger logger, Guid fileId, Guid organizationId, string exceptionType);
}
