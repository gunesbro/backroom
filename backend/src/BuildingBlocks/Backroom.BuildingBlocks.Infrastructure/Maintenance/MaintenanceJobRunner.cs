using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// Runs every registered <see cref="IMaintenanceJob"/> in its own loop as the maintenance database role
/// (docs/adr/0010). A run handles batches, each in its own transaction, until a batch handles fewer rows than
/// the job's batch size or <see cref="MaintenanceJobOptions.MaxBatchesPerRun"/> is reached. A failing run is
/// logged and counted and the job retries at its next interval; a job never stops the host or the other jobs.
/// At most <see cref="MaintenanceJobOptions.MaxPoolSize"/> jobs run at the same time. Logs and spans carry the
/// job name, counts and the exception type and SQLSTATE only, never messages (they can carry row values).
/// </summary>
public sealed partial class MaintenanceJobRunner : BackgroundService, IAsyncDisposable
{
    private readonly List<IMaintenanceJob> _jobs;
    private readonly MaintenanceJobOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MaintenanceJobRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _slots;
    private MaintenanceDataSource? _dataSource;

    public MaintenanceJobRunner(
        IEnumerable<IMaintenanceJob> jobs,
        IOptions<MaintenanceJobOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _jobs = jobs.ToList();
        _slots = new SemaphoreSlim(options.Value.MaxPoolSize);
        _options = options.Value;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MaintenanceJobRunner>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_jobs.Count == 0)
        {
            return;
        }

        _dataSource ??= new MaintenanceDataSource(_options, _loggerFactory);
        // A role that is not fit stops the worker: nothing runs with a role that could do more than it should.
        // An unreachable server is not that: wait for it with a capped back-off.
        await VerifyRoleWithRetryAsync(stoppingToken);
        try
        {
            await Task.WhenAll(_jobs.Select(job => RunLoopAsync(job, stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        _slots.Dispose();
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    /// <summary>Checks that the maintenance role is fit (also done at startup); throws when it is not.</summary>
    internal Task VerifyRoleAsync(CancellationToken cancellationToken)
    {
        _dataSource ??= new MaintenanceDataSource(_options, _loggerFactory);
        return _dataSource.VerifyRoleAsync(cancellationToken);
    }

    /// <summary>
    /// One run of <paramref name="job"/>: batches until one is short or the limit is reached, then the
    /// backlog measurement. Failures are logged and counted, never thrown (except cancellation). Returns the
    /// rows handled.
    /// </summary>
    internal async Task<int> RunOnceAsync(IMaintenanceJob job, CancellationToken cancellationToken)
    {
        _dataSource ??= new MaintenanceDataSource(_options, _loggerFactory);
        // The pool holds MaxPoolSize connections; the other jobs wait here instead of for a connection.
        await _slots.WaitAsync(cancellationToken);
        try
        {
            return await RunOnceCoreAsync(job, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "A failing job must not stop the host or the other jobs; it is logged and retried in its next run.")]
    private async Task<int> RunOnceCoreAsync(IMaintenanceJob job, CancellationToken cancellationToken)
    {
        using var activity = MaintenanceTelemetry.Source.StartActivity("maintenance.run", ActivityKind.Internal);
        activity?.SetTag("maintenance.job", job.Name);

        var total = 0;
        try
        {
            await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
            var batches = 0;
            while (batches < _options.MaxBatchesPerRun)
            {
                var handled = await RunBatchAsync(job, connection, cancellationToken);
                total += handled;
                batches++;
                if (handled < job.BatchSize)
                {
                    break;
                }
            }

            MaintenanceTelemetry.RecordRows(job.Name, total);
            activity?.SetTag("maintenance.rows", total);
            activity?.SetTag("maintenance.batches", batches);
            LogRunFinished(_logger, job.Name, total, batches);

            await MeasureBacklogAsync(job, connection, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            MaintenanceTelemetry.RecordFailedRun(job.Name);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("maintenance.rows", total);
            LogRunFailed(_logger, job.Name, total, exception.GetType().Name, SqlStateOf(exception));
        }

        return total;
    }

    private static async Task<int> RunBatchAsync(IMaintenanceJob job, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var handled = await job.RunBatchAsync(connection, transaction, cancellationToken);
        if (handled < 0 || handled > job.BatchSize)
        {
            throw new InvalidOperationException($"Maintenance job {job.Name} reported {handled} rows for a batch size of {job.BatchSize}.");
        }

        await transaction.CommitAsync(cancellationToken);
        return handled;
    }

    [SuppressMessage("Design", "CA1031", Justification = "Measuring is best effort; a failure must not fail a run that already succeeded.")]
    private async Task MeasureBacklogAsync(IMaintenanceJob job, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (job is not IMaintenanceBacklog probe)
        {
            return;
        }

        try
        {
            var backlog = await probe.MeasureBacklogAsync(connection, cancellationToken);
            var count = Math.Clamp(backlog.Count, 0, MaintenanceBacklog.MaxCount);
            var age = backlog.OldestDue is { } oldest ? _timeProvider.GetUtcNow() - oldest : TimeSpan.Zero;
            MaintenanceTelemetry.RecordBacklog(job.Name, count, age);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            MaintenanceTelemetry.RecordFailedMeasurement(job.Name);
            LogBacklogFailed(_logger, job.Name, exception.GetType().Name, SqlStateOf(exception));
        }
    }

    private async Task RunLoopAsync(IMaintenanceJob job, CancellationToken cancellationToken)
    {
        // Replicas started together would otherwise run in step.
        var startLimit = _options.MaxStartDelay < job.Interval ? _options.MaxStartDelay : job.Interval;
        await Task.Delay(RandomUpTo(startLimit), _timeProvider, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(job, cancellationToken);
            await Task.Delay(job.Interval + RandomUpTo(job.Interval / 10), _timeProvider, cancellationToken);
        }
    }

    private static TimeSpan RandomUpTo(TimeSpan limit)
    {
        // Jitter does not need to be unpredictable, but the security analyzers reject System.Random.
        var fraction = RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000d;
        return limit * fraction;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Maintenance job {Job} handled {Rows} rows in {Batches} batches.")]
    private static partial void LogRunFinished(ILogger logger, string job, int rows, int batches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance job {Job} failed after handling {Rows} rows ({ExceptionType}, SQLSTATE {SqlState}); retrying in its next run.")]
    private static partial void LogRunFailed(ILogger logger, string job, int rows, string exceptionType, string sqlState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Measuring the backlog of maintenance job {Job} failed ({ExceptionType}, SQLSTATE {SqlState}).")]
    private static partial void LogBacklogFailed(ILogger logger, string job, string exceptionType, string sqlState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database for maintenance jobs is not reachable yet ({ExceptionType}, SQLSTATE {SqlState}); retrying in {Seconds} s.")]
    private static partial void LogVerifyRetry(ILogger logger, string exceptionType, string sqlState, int seconds);

    // Exception messages and stack traces can carry row values, so logs get the type and SQLSTATE only.
    private static string SqlStateOf(Exception exception) => exception is PostgresException postgres ? postgres.SqlState : "-";

    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true } or System.Net.Sockets.SocketException or TimeoutException;

    [SuppressMessage("Design", "CA1031", Justification = "Only transient errors are retried; the filter rethrows everything else.")]
    private async Task VerifyRoleWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _dataSource!.VerifyRoleAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
            {
                LogVerifyRetry(_logger, exception.GetType().Name, SqlStateOf(exception), (int)delay.TotalSeconds);
                await Task.Delay(delay, _timeProvider, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}
