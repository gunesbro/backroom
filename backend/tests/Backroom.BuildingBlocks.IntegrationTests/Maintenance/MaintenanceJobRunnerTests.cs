using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.IntegrationTests.Fencing;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;
using Backroom.Testing.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.IntegrationTests.Maintenance;

/// <summary>The runner against a real database, connected as the maintenance role.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MaintenanceJobRunnerTests(PostgresDatabase database) : IAsyncLifetime
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await NotesSchema.PrepareAsync(database);

        // What the migration of a module that owns a job does: the columns the job needs, nothing else. FOR UPDATE
        // SKIP LOCKED needs UPDATE on at least one column, so a deleting job gets it on its least sensitive one.
        await database.ExecuteAsMigratorAsync(
            MaintenanceGrants.GrantColumnsSql(NotesSchema.Schema, NotesSchema.Table, ["id"], ["id"]) + MaintenanceGrants.GrantDeleteSql(NotesSchema.Schema, NotesSchema.Table));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RunOnce_WithMoreRowsThanABatch_RunsBatchesUntilOneIsShortAsync()
    {
        await SeedAsync(25);
        var job = new DeleteNotesJob(batchSize: 10);
        await using var runner = CreateRunner(job);

        var handled = await runner.RunOnceAsync(job, CancellationToken);

        handled.Should().Be(25);
        job.Batches.Should().Be(3);
        (await CountNotesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunOnce_WhenEveryBatchIsFull_StopsAtTheBatchLimitAsync()
    {
        await SeedAsync(25);
        var job = new DeleteNotesJob(batchSize: 10);
        await using var runner = CreateRunner(job, maxBatchesPerRun: 2);

        var handled = await runner.RunOnceAsync(job, CancellationToken);

        handled.Should().Be(20);
        job.Batches.Should().Be(2);
        (await CountNotesAsync()).Should().Be(5);
    }

    [Fact]
    public async Task RunOnce_WhileTheCellsWritesAreFenced_SkipsTheRunAsync()
    {
        await SeedAsync(3);
        var job = new DeleteNotesJob(batchSize: 10);
        var fence = new SwitchableFence { Open = false };
        await using var runner = CreateRunner(job, fence: fence);

        (await runner.RunOnceAsync(job, CancellationToken)).Should().Be(0);
        job.Batches.Should().Be(0);
        (await CountNotesAsync()).Should().Be(3);

        fence.Open = true;
        (await runner.RunOnceAsync(job, CancellationToken)).Should().Be(3);
    }

    [Fact]
    public async Task RunOnce_WhenTheTableIsEmpty_RunsOneBatchAsync()
    {
        var job = new DeleteNotesJob(batchSize: 10);
        await using var runner = CreateRunner(job);

        (await runner.RunOnceAsync(job, CancellationToken)).Should().Be(0);

        job.Batches.Should().Be(1);
    }

    [Fact]
    public async Task RunOnce_WithRowsOfSeveralOrganizations_HandlesThemAllAsync()
    {
        await SeedAsync(3, Guid.CreateVersion7());
        await SeedAsync(4, Guid.CreateVersion7());
        var job = new DeleteNotesJob(batchSize: 100);
        await using var runner = CreateRunner(job);

        (await runner.RunOnceAsync(job, CancellationToken)).Should().Be(7);
    }

    [Fact]
    public async Task RunOnce_WhenALaterBatchFails_KeepsTheEarlierBatchesAndDoesNotThrowAsync()
    {
        await SeedAsync(25);
        var job = new DeleteNotesJob(batchSize: 10) { FailOnBatch = 2 };
        await using var runner = CreateRunner(job);

        var run = () => runner.RunOnceAsync(job, CancellationToken);

        (await run.Should().NotThrowAsync()).Which.Should().Be(10);
        (await CountNotesAsync()).Should().Be(15);
    }

    [Fact]
    public async Task RunOnce_WhenAJobFails_CountsTheFailedRunAndRunsAgainAsync()
    {
        await SeedAsync(5);
        var job = new DeleteNotesJob(batchSize: 10) { FailOnBatch = 1 };
        await using var runner = CreateRunner(job);
        var failedRuns = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MaintenanceTelemetry.SourceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "backroom.maintenance.failed_runs" && tags.ToArray().Any(tag => Equals(tag.Value, job.Name)))
            {
                Interlocked.Add(ref failedRuns, value);
            }
        });
        listener.Start();

        await runner.RunOnceAsync(job, CancellationToken);
        job.FailOnBatch = 0;
        var second = await runner.RunOnceAsync(job, CancellationToken);

        failedRuns.Should().Be(1);
        second.Should().Be(5);
    }

    [Fact]
    public async Task RunOnce_WhenAJobReportsMoreRowsThanItsBatchSize_FailsTheRunAndRollsBackAsync()
    {
        await SeedAsync(5);
        var job = new DeleteNotesJob(batchSize: 5) { ReportedExtra = 1 };
        await using var runner = CreateRunner(job);

        (await runner.RunOnceAsync(job, CancellationToken)).Should().Be(0);

        (await CountNotesAsync()).Should().Be(5);
    }

    [Fact]
    public async Task RunOnce_WhenTheJobImplementsBacklog_PublishesBacklogAndOldestDueAgeAsync()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var job = new DeleteNotesJob(batchSize: 10) { Backlog = new MaintenanceBacklog(42, time.GetUtcNow() - TimeSpan.FromHours(3)) };
        await using var runner = CreateRunner(job, time: time);
        var measurements = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MaintenanceTelemetry.SourceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        listener.Start();

        await runner.RunOnceAsync(job, CancellationToken);
        listener.RecordObservableInstruments();

        measurements["backroom.maintenance.backlog"].Should().Be(42);
        measurements["backroom.maintenance.oldest_due_age"].Should().Be(TimeSpan.FromHours(3).TotalSeconds);

        void Record(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "job" && Equals(tag.Value, job.Name))
                {
                    measurements[name] = value;
                }
            }
        }
    }

    [Fact]
    public async Task Runner_WhenAJobKeepsFailing_KeepsRunningItsLoopAsync()
    {
        await SeedAsync(5);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var job = new DeleteNotesJob(batchSize: 10) { Interval = TimeSpan.FromSeconds(10), FailOnBatch = 1 };
        await using var runner = CreateRunner(job, maxStartDelay: TimeSpan.Zero, time: time);

        await runner.StartAsync(CancellationToken);
        await AdvanceUntilAsync(time, async () => await CountNotesAsync() == 0);
        await runner.StopAsync(CancellationToken);

        job.Batches.Should().BeGreaterThan(1);
        (await CountNotesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Runner_WhenTheDatabaseIsNotReachable_RetriesInsteadOfStoppingAsync()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var job = new DeleteNotesJob(batchSize: 10);
        var unreachable = new NpgsqlConnectionStringBuilder(database.MaintenanceConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2 }.ConnectionString;
        await using var runner = CreateRunner(job, time: time, connectionString: unreachable);

        await runner.StartAsync(CancellationToken);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(100, CancellationToken);
            time.Advance(TimeSpan.FromSeconds(31));
        }

        runner.ExecuteTask!.IsCompleted.Should().BeFalse();
        await runner.StopAsync(CancellationToken);
        job.Batches.Should().Be(0);
    }

    private static async Task AdvanceUntilAsync(Microsoft.Extensions.Time.Testing.FakeTimeProvider time, Func<Task<bool>> done)
    {
        // Each step moves the fake clock past one interval plus its jitter and gives the loop a moment to run.
        for (var step = 0; step < 100 && !await done(); step++)
        {
            await Task.Delay(100, CancellationToken);
            time.Advance(TimeSpan.FromSeconds(11));
        }
    }

    [Fact]
    public async Task Runner_WhenTheRoleIsNotFit_StopsInsteadOfRunningAsync()
    {
        var job = new DeleteNotesJob(batchSize: 10);
        await using var runner = CreateRunner(job, connectionString: database.SuperuserConnectionString);

        var start = async () =>
        {
            await runner.StartAsync(CancellationToken);
            await runner.ExecuteTask!;
        };

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BYPASSRLS*");
        job.Batches.Should().Be(0);
    }

    private async Task SeedAsync(int count, Guid? organization = null)
    {
        var org = organization ?? Guid.CreateVersion7();
        for (var index = 0; index < count; index++)
        {
            await NotesSchema.SeedAsync(database, org, $"note {index}");
        }
    }

    private async Task<long> CountNotesAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {NotesSchema.Schema}.{NotesSchema.Table}", connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    private MaintenanceJobRunner CreateRunner(
        DeleteNotesJob job,
        int maxBatchesPerRun = 20,
        TimeSpan? maxStartDelay = null,
        TimeProvider? time = null,
        string? connectionString = null,
        SwitchableFence? fence = null)
    {
        var options = new MaintenanceJobOptions
        {
            ConnectionString = connectionString ?? database.MaintenanceConnectionString,
            MaxBatchesPerRun = maxBatchesPerRun,
            MaxStartDelay = maxStartDelay ?? TimeSpan.FromSeconds(30),
        };
        return new MaintenanceJobRunner([job], Options.Create(options), time ?? TimeProvider.System, NullLoggerFactory.Instance, fence);
    }

    /// <summary>Deletes notes in batches, the way a retention job does: LIMIT and SKIP LOCKED inside the batch's transaction.</summary>
    private sealed class DeleteNotesJob(int batchSize) : IMaintenanceJob, IMaintenanceBacklog
    {
        private int _batches;

        public string Name => "rls_test.delete-notes";

        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

        public int BatchSize => batchSize;

        public int Batches => Volatile.Read(ref _batches);

        /// <summary>The batch (1-based, counted over all runs) that throws after deleting; 0 for none.</summary>
        public int FailOnBatch { get; set; }

        public int ReportedExtra { get; init; }

        public MaintenanceBacklog? Backlog { get; init; }

        public async Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            var batch = Interlocked.Increment(ref _batches);

            var handled = await DeleteAsync(connection, transaction, cancellationToken);
            if (batch == FailOnBatch)
            {
                throw new InvalidOperationException("The job failed on purpose.");
            }

            return handled + ReportedExtra;
        }

        public Task<MaintenanceBacklog> MeasureBacklogAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(Backlog ?? new MaintenanceBacklog(0, null));

        private async Task<int> DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {NotesSchema.Schema}.{NotesSchema.Table} WHERE id IN (SELECT id FROM {NotesSchema.Schema}.{NotesSchema.Table} ORDER BY id LIMIT $1 FOR UPDATE SKIP LOCKED)",
                connection,
                transaction);
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
