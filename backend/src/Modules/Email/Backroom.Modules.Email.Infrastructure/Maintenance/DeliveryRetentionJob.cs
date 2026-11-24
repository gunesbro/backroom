using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Backroom.Modules.Email.Infrastructure.Maintenance;

/// <summary>
/// Deletes deliveries older than <see cref="Retention"/>, counted from <c>created_at</c>, whatever their status
/// (docs/contracts/phase-1.md, "Privacy and retention"; docs/adr/0010). Runs as the maintenance role, which reads only
/// <c>id</c> and <c>created_at</c> here (never the recipient hash) and holds <c>DELETE</c>. Rows another transaction
/// holds are skipped, never waited for.
/// </summary>
internal sealed partial class DeliveryRetentionJob(TimeProvider timeProvider, ILogger<DeliveryRetentionJob> logger, int batchSize = DeliveryRetentionJob.DefaultBatchSize)
    : IMaintenanceJob, IMaintenanceBacklog
{
    public const string JobName = "email.delivery-retention";

    public const int DefaultBatchSize = 250;

    /// <summary>How long the delivery log keeps a row.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    // By deliveries_created_at_idx. $1: created_at cutoff, $2: limit.
    private const string DeleteSql = """
        WITH old AS MATERIALIZED (
            SELECT id FROM email.deliveries
            WHERE created_at <= $1
            ORDER BY created_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        DELETE FROM email.deliveries d
        USING old
        WHERE d.id = old.id
        """;

    // $1: cutoff, $2: the bound.
    private const string BacklogSql = """
        SELECT count(*)::integer, min(created_at)
        FROM (SELECT created_at FROM email.deliveries
              WHERE created_at <= $1
              ORDER BY created_at
              LIMIT $2) due
        """;

    public string Name => JobName;

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public int BatchSize => batchSize;

    public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(DeleteSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() - Retention });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (deleted > 0)
        {
            LogDeleted(logger, JobName, deleted);
        }

        return deleted;
    }

    public async Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(BacklogSql, connection);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() - Retention });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = MaintenanceBacklog.MaxCount });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oldest = await reader.IsDBNullAsync(1, cancellationToken) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1) + Retention;
        return new MaintenanceBacklog(reader.GetInt32(0), oldest);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} deleted {Count} deliveries past their retention.")]
    private static partial void LogDeleted(ILogger logger, string job, int count);
}
