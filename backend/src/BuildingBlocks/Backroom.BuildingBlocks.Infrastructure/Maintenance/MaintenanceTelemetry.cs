using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// Spans and metrics of maintenance job runs (docs/adr/0010). Tags carry the job name only, never row values.
/// An alert fires when <c>backroom.maintenance.oldest_due_age</c> exceeds a job's allowed lag (by default one
/// day).
/// </summary>
public static class MaintenanceTelemetry
{
    public const string SourceName = "Backroom.Maintenance";

    internal static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter s_meter = new(SourceName);

    private static readonly Counter<long> s_rowsHandled =
        s_meter.CreateCounter<long>("backroom.maintenance.rows_handled", "{row}", "Rows handled by maintenance job runs.");

    private static readonly Counter<long> s_failedRuns =
        s_meter.CreateCounter<long>("backroom.maintenance.failed_runs", "{run}", "Maintenance job runs that failed.");

    private static readonly Counter<long> s_failedMeasurements =
        s_meter.CreateCounter<long>("backroom.maintenance.failed_measurements", "{measurement}", "Backlog measurements that failed.");

    // One entry per registered job, so the dictionary is bounded by the number of jobs.
    private static readonly ConcurrentDictionary<string, (long Backlog, double OldestDueAgeSeconds)> s_latest = new(StringComparer.Ordinal);

    internal static readonly ObservableGauge<long> s_backlog = s_meter.CreateObservableGauge(
        "backroom.maintenance.backlog",
        () => s_latest.Select(entry => new Measurement<long>(entry.Value.Backlog, new KeyValuePair<string, object?>("job", entry.Key))),
        "{row}",
        "Due rows left after the job's last run (capped).");

    internal static readonly ObservableGauge<double> s_oldestDueAge = s_meter.CreateObservableGauge(
        "backroom.maintenance.oldest_due_age",
        () => s_latest.Select(entry => new Measurement<double>(entry.Value.OldestDueAgeSeconds, new KeyValuePair<string, object?>("job", entry.Key))),
        "s",
        "Age of the oldest due row after the job's last run; 0 when nothing is due.");

    internal static void RecordRows(string job, int rows) =>
        s_rowsHandled.Add(rows, new KeyValuePair<string, object?>("job", job));

    internal static void RecordFailedRun(string job) =>
        s_failedRuns.Add(1, new KeyValuePair<string, object?>("job", job));

    internal static void RecordBacklog(string job, int backlog, TimeSpan oldestDueAge) =>
        s_latest[job] = (backlog, Math.Max(0, oldestDueAge.TotalSeconds));

    internal static void RecordFailedMeasurement(string job)
    {
        // A stale value would hide that the job is not being measured.
        s_latest.TryRemove(job, out _);
        s_failedMeasurements.Add(1, new KeyValuePair<string, object?>("job", job));
    }
}
