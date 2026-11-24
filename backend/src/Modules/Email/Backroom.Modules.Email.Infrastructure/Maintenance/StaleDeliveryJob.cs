using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Backroom.Modules.Email.Infrastructure.Maintenance;

/// <summary>
/// Closes <c>pending</c> deliveries whose last update is older than <see cref="StaleAfter"/> as <c>unknown</c>
/// (docs/adr/0010). A row left <c>pending</c> means the outcome is unknown (the process stopped after the hand-over to
/// the server), so it is never resent and never closed as <c>failed</c>.
/// <para>
/// Why not <c>failed</c>: the partial unique index <c>deliveries_reference_once_idx</c> lets a new row in for a reference
/// whose rows are all <c>failed</c>. If the event's transaction was lost after the hand-over (a crash), the inbox has no
/// record of the event, so a redelivery after this job ran (a long outage, a replay from the dead-letter queue) would
/// send a second invitation. <c>unknown</c> is not <c>failed</c>, so the index still blocks that send.
/// </para>
/// Runs as the maintenance role, which reads only <c>id</c>, <c>status</c> and <c>updated_at</c> (never the recipient)
/// and updates <c>status</c> and <c>updated_at</c>. Rows another transaction holds are skipped, never waited for.
/// </summary>
internal sealed partial class StaleDeliveryJob(TimeProvider timeProvider, ILogger<StaleDeliveryJob> logger, int batchSize = StaleDeliveryJob.DefaultBatchSize)
    : IMaintenanceJob, IMaintenanceBacklog
{
    public const string JobName = "email.delivery-stale-pending";

    public const int DefaultBatchSize = 250;

    /// <summary>
    /// How long a delivery may stay <c>pending</c>. Clearly larger than the send timeout (30 s per SMTP operation,
    /// <c>SmtpEmailSender.OperationTimeout</c>), so a send that is still running is never closed.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    // By deliveries_pending_idx. $1: updated_at cutoff, $2: limit, $3: when it was closed.
    private const string CloseSql = """
        WITH due AS MATERIALIZED (
            SELECT id FROM email.deliveries
            WHERE status = 'pending' AND updated_at <= $1
            ORDER BY updated_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        UPDATE email.deliveries d SET status = 'unknown', updated_at = $3
        FROM due
        WHERE d.id = due.id AND d.status = 'pending'
        """;

    // $1: cutoff, $2: the bound.
    private const string BacklogSql = """
        SELECT count(*)::integer, min(updated_at)
        FROM (SELECT updated_at FROM email.deliveries
              WHERE status = 'pending' AND updated_at <= $1
              ORDER BY updated_at
              LIMIT $2) due
        """;

    public string Name => JobName;

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public int BatchSize => batchSize;

    public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var now = timeProvider.GetUtcNow();
        await using var command = new NpgsqlCommand(CloseSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now - StaleAfter });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        var closed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (closed > 0)
        {
            LogClosed(logger, JobName, closed);
        }

        return closed;
    }

    public async Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(BacklogSql, connection);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() - StaleAfter });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = MaintenanceBacklog.MaxCount });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oldest = await reader.IsDBNullAsync(1, cancellationToken) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1) + StaleAfter;
        return new MaintenanceBacklog(reader.GetInt32(0), oldest);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} closed {Count} stale pending deliveries as unknown.")]
    private static partial void LogClosed(ILogger logger, string job, int count);
}
