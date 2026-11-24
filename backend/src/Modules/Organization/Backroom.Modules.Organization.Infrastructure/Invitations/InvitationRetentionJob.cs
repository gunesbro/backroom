using System.Diagnostics.CodeAnalysis;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// Erases the invited email of <c>expired</c> and <c>revoked</c> invitations <see cref="Retention"/> after they
/// reached that state (docs/contracts/phase-1.md, "Privacy and retention"; docs/adr/0010). Runs as the maintenance
/// role, which has no <c>SELECT</c> on <c>email</c>: it finds its rows through <c>email_erased_at IS NULL</c> and
/// sets <c>email = NULL, email_erased_at = $now</c>, so a second run finds nothing. Rows another transaction holds
/// are skipped, never waited for. No event is published.
/// </summary>
internal sealed partial class InvitationRetentionJob(TimeProvider timeProvider, ILogger<InvitationRetentionJob> logger, int batchSize = InvitationRetentionJob.DefaultBatchSize)
    : IMaintenanceJob, IMaintenanceBacklog
{
    public const string JobName = "organization.invitation-retention";

    public const int DefaultBatchSize = 250;

    /// <summary>How long expired and revoked invitations keep the invited email.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    // By invitations_expired_email_idx. $1: reached-the-state cutoff, $2: limit, $3: when it was erased.
    private const string EraseExpiredSql = """
        WITH ended AS MATERIALIZED (
            SELECT id FROM organization.invitations
            WHERE status = 'expired' AND email_erased_at IS NULL AND expired_at <= $1
            ORDER BY expired_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        UPDATE organization.invitations i SET email = NULL, email_erased_at = $3
        FROM ended
        WHERE i.id = ended.id AND i.status = 'expired'
        """;

    // By invitations_revoked_email_idx.
    private const string EraseRevokedSql = """
        WITH ended AS MATERIALIZED (
            SELECT id FROM organization.invitations
            WHERE status = 'revoked' AND email_erased_at IS NULL AND revoked_at <= $1
            ORDER BY revoked_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        UPDATE organization.invitations i SET email = NULL, email_erased_at = $3
        FROM ended
        WHERE i.id = ended.id AND i.status = 'revoked'
        """;

    // $1: cutoff, $2: the bound of each part.
    private const string BacklogSql = """
        SELECT count(*)::integer, min(ended_at)
        FROM (
            (SELECT expired_at AS ended_at FROM organization.invitations
             WHERE status = 'expired' AND email_erased_at IS NULL AND expired_at <= $1
             ORDER BY expired_at LIMIT $2)
            UNION ALL
            (SELECT revoked_at FROM organization.invitations
             WHERE status = 'revoked' AND email_erased_at IS NULL AND revoked_at <= $1
             ORDER BY revoked_at LIMIT $2)) due
        """;

    public string Name => JobName;

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public int BatchSize => batchSize;

    public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var now = timeProvider.GetUtcNow();
        var cutoff = now - Retention;
        var erased = await EraseAsync(EraseExpiredSql, connection, transaction, cutoff, batchSize, now, cancellationToken);

        // Together at most one batch, so the runner's "a short batch means nothing more is due" holds.
        if (erased < batchSize)
        {
            erased += await EraseAsync(EraseRevokedSql, connection, transaction, cutoff, batchSize - erased, now, cancellationToken);
        }

        if (erased > 0)
        {
            LogRetention(logger, erased);
        }

        return erased;
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

    [SuppressMessage("Security", "CA2100", Justification = "Only the two constant statements of this class are passed in; values are parameters.")]
    private static async Task<int> EraseAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset cutoff,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = cutoff });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 51, Level = LogLevel.Information, Message = "Invitation retention erased the invited email of {Count} invitations.")]
    private static partial void LogRetention(ILogger logger, int count);
}
