using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// Optional addition to <see cref="IMaintenanceJob"/>: lets the runner report how far behind the job is
/// (docs/adr/0010). Retention that falls behind is a privacy breach nobody sees, so every job that deletes
/// or erases data should implement it. The runner calls it after each run and publishes the values as metrics.
/// </summary>
public interface IMaintenanceBacklog
{
    /// <summary>
    /// Counts the rows that are due now and finds the earliest due time. Bound it with a <c>LIMIT</c> of
    /// <see cref="MaintenanceBacklog.MaxCount"/>, for example
    /// <c>SELECT count(*), min(expires_at) FROM (SELECT expires_at FROM t WHERE &lt;due&gt; LIMIT 10000) d</c>,
    /// and read only the columns the job's grants allow.
    /// </summary>
    Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken);
}

/// <summary>Due rows left after a run; <paramref name="OldestDue"/> is when the oldest of them became due.</summary>
public readonly record struct MaintenanceBacklog(int Count, DateTimeOffset? OldestDue)
{
    /// <summary>The count is capped here; a larger backlog reports this value.</summary>
    public const int MaxCount = 10_000;
}
