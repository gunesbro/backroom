namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>Settings of the maintenance job runner, which runs in the worker role (docs/adr/0010).</summary>
public sealed class MaintenanceJobOptions
{
    public const string SectionName = "MaintenanceJobs";

    /// <summary>Largest <see cref="IMaintenanceJob.BatchSize"/> a job may declare.</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>
    /// Connection string of the maintenance role (<c>backroom_maintenance</c>); from secrets, like every
    /// connection string. Required as soon as a job is registered.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Connections of the runner's own pool; it is also the number of jobs that run at the same time.</summary>
    public int MaxPoolSize { get; set; } = 2;

    /// <summary>Most batches of one job per run; whatever is left waits for the next run.</summary>
    public int MaxBatchesPerRun { get; set; } = 20;

    /// <summary>
    /// Longest random delay before a job's first run, so replicas started together do not run in step
    /// (capped at the job's interval).
    /// </summary>
    public TimeSpan MaxStartDelay { get; set; } = TimeSpan.FromSeconds(30);
}
