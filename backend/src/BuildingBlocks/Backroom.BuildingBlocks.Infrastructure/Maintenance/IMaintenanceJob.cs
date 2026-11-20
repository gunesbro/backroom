using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// A cross-organization maintenance job (docs/adr/0010), such as marking due rows expired or deleting
/// data past its retention. The worker's <see cref="MaintenanceServiceCollectionExtensions.AddMaintenanceJobRunner"/>
/// runs it every <see cref="Interval"/> as the <c>backroom_maintenance</c> database role, which bypasses
/// row-level security but holds only the column privileges the job's module granted in its migration
/// (<see cref="MaintenanceGrants"/>). Register it with <see cref="MaintenanceServiceCollectionExtensions.AddMaintenanceJob{TJob}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each batch must be bounded and must not wait for rows the application holds: select the rows with
/// <c>LIMIT</c> (at most <see cref="BatchSize"/>) and <c>FOR UPDATE SKIP LOCKED</c>, through an index
/// (usually partial) on the filter, and change only those rows, for example
/// <c>UPDATE t SET status = 'expired' WHERE id IN (SELECT id FROM t WHERE status = 'pending' AND expires_at &lt;= $1 ORDER BY expires_at LIMIT $2 FOR UPDATE SKIP LOCKED)</c>.
/// Take the time as a parameter from <see cref="TimeProvider"/>, not from the database clock.
/// <c>FOR UPDATE</c> needs <c>UPDATE</c> privilege on at least one column of the table, so a job that only deletes
/// still gets <c>UPDATE</c> on its least sensitive column (a status or timestamp, never content).
/// </para>
/// <para>
/// The role sees every organization's rows: read only the columns the job needs to decide and act, never
/// content (message bodies, file names, email addresses). Return counts, never rows; log ids and counts only.
/// </para>
/// </remarks>
public interface IMaintenanceJob
{
    /// <summary>
    /// Stable name for logs and traces: <c>{module}.{job-name}</c>, for example
    /// <c>organization.invitation-expiry</c>. Unique per host.
    /// </summary>
    string Name { get; }

    /// <summary>Time between runs (10 seconds to 1 day); the runner adds up to a tenth of it as jitter.</summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Most rows one batch handles (1 to <see cref="MaintenanceJobOptions.MaxBatchSize"/>); it is the
    /// <c>LIMIT</c> of the batch. A batch that handles exactly this many is followed by another one in
    /// the same run, up to <see cref="MaintenanceJobOptions.MaxBatchesPerRun"/>.
    /// </summary>
    int BatchSize { get; }

    /// <summary>
    /// Handles one batch inside <paramref name="transaction"/>, which the runner commits afterwards, and
    /// returns the number of rows it handled (0 to <see cref="BatchSize"/>).
    /// </summary>
    Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);
}
