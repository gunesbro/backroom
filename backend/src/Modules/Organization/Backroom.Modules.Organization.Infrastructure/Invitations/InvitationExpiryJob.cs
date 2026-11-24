using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// Marks pending invitations <c>expired</c> (docs/contracts/phase-1.md, flow 3, step 8; docs/adr/0010): only
/// <see cref="ExpiryGrace"/> after <c>expires_at</c>, so an acceptance the control plane recorded in time but
/// delivered late still succeeds (the control plane expires at <c>expires_at</c>). Runs as the maintenance role,
/// which reads only state and time columns (never the email). Rows another transaction holds are skipped, never
/// waited for, so several instances are safe. No event is published: the contract names none for the cell's
/// expiry, and the control plane has expired it already.
/// </summary>
internal sealed partial class InvitationExpiryJob(TimeProvider timeProvider, ILogger<InvitationExpiryJob> logger, int batchSize = InvitationExpiryJob.DefaultBatchSize)
    : IMaintenanceJob, IMaintenanceBacklog
{
    public const string JobName = "organization.invitation-expiry";

    public const int DefaultBatchSize = 250;

    /// <summary>How long after <c>expires_at</c> a pending invitation is marked expired.</summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromHours(24);

    // By invitations_pending_expiry_idx. $1: expires_at cutoff, $2: limit, $3: when it was marked.
    private const string ExpireSql = """
        WITH due AS MATERIALIZED (
            SELECT id FROM organization.invitations
            WHERE status = 'pending' AND expires_at <= $1
            ORDER BY expires_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        UPDATE organization.invitations i SET status = 'expired', expired_at = $3
        FROM due
        WHERE i.id = due.id AND i.status = 'pending'
        RETURNING i.id, i.org_id
        """;

    // $1: expires_at cutoff, $2: the bound.
    private const string BacklogSql = """
        SELECT count(*)::integer, min(expires_at)
        FROM (SELECT expires_at FROM organization.invitations
              WHERE status = 'pending' AND expires_at <= $1
              ORDER BY expires_at
              LIMIT $2) due
        """;

    public string Name => JobName;

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public int BatchSize => batchSize;

    public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var now = timeProvider.GetUtcNow();
        await using var command = new NpgsqlCommand(ExpireSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now - ExpiryGrace });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        var expired = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            expired++;

            // Ids only, never the invited address. Bounded by the batch limit.
            var invitationId = reader.GetGuid(0);
            var organizationId = reader.GetGuid(1);
            LogInvitationExpired(logger, invitationId, organizationId);
        }

        return expired;
    }

    public async Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(BacklogSql, connection);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() - ExpiryGrace });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = MaintenanceBacklog.MaxCount });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oldest = await reader.IsDBNullAsync(1, cancellationToken) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1) + ExpiryGrace;
        return new MaintenanceBacklog(reader.GetInt32(0), oldest);
    }

    [LoggerMessage(EventId = 53, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} of organization {OrganizationId} expired.")]
    private static partial void LogInvitationExpired(ILogger logger, Guid invitationId, Guid organizationId);
}
