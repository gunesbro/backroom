using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// The cell's scheduled invitation work (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and retention"):
/// a pending invitation becomes <c>expired</c> only <see cref="ExpiryGrace"/> after <c>expires_at</c>, so an
/// acceptance the control plane recorded in time but delivered late still succeeds (the control plane expires
/// at <c>expires_at</c>); the invited email of expired and revoked invitations is deleted <see cref="Retention"/>
/// after they reached that state. Each batch is its own short transaction and a run stops after
/// <c>MaxBatchesPerRun</c> batches; whatever is left waits for the next run. Safe with several instances: rows
/// another transaction holds are skipped, never waited for.
/// </summary>
/// <remarks>
/// The jobs work across all organizations, so they run as the relay role (the data source's role), which has
/// BYPASSRLS, owns nothing and may read and change only the columns the migration grants: the same mechanism as
/// the outbox relay and the link delivery. They use the application clock (<see cref="TimeProvider"/>). Expiry
/// publishes no event: the contract names none for the cell's expiry, and the control plane has expired it
/// already.
/// </remarks>
internal sealed partial class InvitationJobs(
    [FromKeyedServices(InvitationJobs.DataSourceKey)] NpgsqlDataSource dataSource,
    IOptions<InvitationJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<InvitationJobs> logger)
{
    /// <summary>The keyed service name of the relay role's data source.</summary>
    public const string DataSourceKey = "organization-invitation-jobs";

    public const string ActivitySourceName = "Backroom.Organization";

    /// <summary>How long after <c>expires_at</c> a pending invitation is marked expired.</summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromHours(24);

    /// <summary>How long expired and revoked invitations keep the invited email.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    // By invitations_pending_expiry_idx. $1: expires_at cutoff, $2: limit, $3: when it was marked.
    private const string ExpireSql = """
        WITH due AS MATERIALIZED (
            SELECT id FROM organization.invitations
            WHERE status = 'pending' AND expires_at <= $1
            ORDER BY expires_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED),
        expired AS (
            UPDATE organization.invitations i SET status = 'expired', expired_at = $3
            FROM due
            WHERE i.id = due.id AND i.status = 'pending'
            RETURNING i.id, i.org_id)
        SELECT selected.count, expired.id, expired.org_id
        FROM (SELECT count(*)::integer AS count FROM due) selected
        LEFT JOIN expired ON true
        """;

    // By invitations_expired_email_idx. $1: reached-the-state cutoff, $2: limit.
    private const string ClearExpiredEmailSql = """
        WITH ended AS MATERIALIZED (
            SELECT id FROM organization.invitations
            WHERE status = 'expired' AND email IS NOT NULL AND expired_at <= $1
            ORDER BY expired_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED),
        cleared AS (
            UPDATE organization.invitations i SET email = NULL
            FROM ended
            WHERE i.id = ended.id AND i.status = 'expired'
            RETURNING 1)
        SELECT (SELECT count(*)::integer FROM ended), (SELECT count(*)::integer FROM cleared)
        """;

    // By invitations_revoked_email_idx.
    private const string ClearRevokedEmailSql = """
        WITH ended AS MATERIALIZED (
            SELECT id FROM organization.invitations
            WHERE status = 'revoked' AND email IS NOT NULL AND revoked_at <= $1
            ORDER BY revoked_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED),
        cleared AS (
            UPDATE organization.invitations i SET email = NULL
            FROM ended
            WHERE i.id = ended.id AND i.status = 'revoked'
            RETURNING 1)
        SELECT (SELECT count(*)::integer FROM ended), (SELECT count(*)::integer FROM cleared)
        """;

    /// <summary>Marks due pending invitations expired; returns how many.</summary>
    public Task<int> ExpireAsync(CancellationToken cancellationToken) =>
        ExpireAsync(options.Value.BatchSize, options.Value.MaxBatchesPerRun, cancellationToken);

    internal async Task<int> ExpireAsync(int batchSize, int maxBatches, CancellationToken cancellationToken)
    {
        using var activity = s_activitySource.StartActivity("invitation expiry");
        var now = timeProvider.GetUtcNow();
        var expired = 0;
        await RunInBatchesAsync(
            async token =>
            {
                await using var command = dataSource.CreateCommand(ExpireSql);
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now - ExpiryGrace });
                command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
                return await ExecuteExpiryAsync(command, token);
            },
            batchSize,
            maxBatches,
            changed => expired += changed,
            cancellationToken);
        activity?.SetTag("organization.invitations.expired", expired);
        if (expired > 0)
        {
            LogExpired(logger, expired);
        }

        return expired;
    }

    /// <summary>Deletes the invited email of expired and revoked invitations past the retention; returns how many.</summary>
    public Task<int> ApplyRetentionAsync(CancellationToken cancellationToken) =>
        ApplyRetentionAsync(options.Value.BatchSize, options.Value.MaxBatchesPerRun, cancellationToken);

    internal async Task<int> ApplyRetentionAsync(int batchSize, int maxBatches, CancellationToken cancellationToken)
    {
        using var activity = s_activitySource.StartActivity("invitation retention");
        var cutoff = timeProvider.GetUtcNow() - Retention;
        var cleared = 0;
        foreach (var sql in new[] { ClearExpiredEmailSql, ClearRevokedEmailSql })
        {
            await RunInBatchesAsync(
                async token =>
                {
                    await using var command = dataSource.CreateCommand(sql);
                    command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = cutoff });
                    command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
                    await using var reader = await command.ExecuteReaderAsync(token);
                    await reader.ReadAsync(token);
                    return (reader.GetInt32(0), reader.GetInt32(1));
                },
                batchSize,
                maxBatches,
                changed => cleared += changed,
                cancellationToken);
        }

        activity?.SetTag("organization.invitations.emails_deleted", cleared);
        if (cleared > 0)
        {
            LogRetention(logger, cleared);
        }

        return cleared;
    }

    private async Task<(int Selected, int Changed)> ExecuteExpiryAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var selected = 0;
        var changed = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            selected = reader.GetInt32(0);
            if (!await reader.IsDBNullAsync(1, cancellationToken))
            {
                changed++;

                // Ids only, never the invited address. Bounded by the batch limit.
                var invitationId = reader.GetGuid(1);
                var organizationId = reader.GetGuid(2);
                LogInvitationExpired(logger, invitationId, organizationId);
            }
        }

        return (selected, changed);
    }

    // Each statement is its own transaction (a data source command runs in autocommit).
    private static async Task RunInBatchesAsync(
        Func<CancellationToken, Task<(int Selected, int Changed)>> batch,
        int batchSize,
        int maxBatches,
        Action<int> changed,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatches);
        for (var round = 0; round < maxBatches; round++)
        {
            var (selected, done) = await batch(cancellationToken);
            changed(done);

            // Fewer selected than the limit: nothing more is due. Busy rows the batch skipped do not end the run.
            if (selected < batchSize)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 50, Level = LogLevel.Information, Message = "Marked {Count} invitations expired.")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(EventId = 51, Level = LogLevel.Information, Message = "Invitation retention deleted the invited email of {Count} invitations.")]
    private static partial void LogRetention(ILogger logger, int count);

    [LoggerMessage(EventId = 53, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} of organization {OrganizationId} expired.")]
    private static partial void LogInvitationExpired(ILogger logger, Guid invitationId, Guid organizationId);
}
