using System.Collections.Concurrent;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Application.Invitations;
using Backroom.Modules.Email.Infrastructure.Maintenance;
using Backroom.Modules.Organization.Contracts;
using Backroom.Testing.Mailpit;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>
/// The delivery log's maintenance jobs (docs/adr/0010) against real PostgreSQL, run as the maintenance role like the
/// worker. The clock is a fake one in 2001: the jobs only reach rows with timestamps of that year, which only this
/// class writes, so other classes' deliveries are never touched. Each test deletes its organization's rows afterwards.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class DeliveryJobsTests(PostgresDatabase database, MailpitServer mailpit) : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] s_forbiddenReads =
    [
        "SELECT recipient_hash FROM email.deliveries LIMIT 1",
        "SELECT provider_message_id FROM email.deliveries LIMIT 1",
        "SELECT reference_id FROM email.deliveries LIMIT 1",
        "SELECT reference_version FROM email.deliveries LIMIT 1",
        "SELECT template FROM email.deliveries LIMIT 1",
    ];

    private readonly FakeTimeProvider _time = new(s_now);
    private readonly Guid _organization = Guid.CreateVersion7();
    private readonly List<NpgsqlDataSource> _dataSources = [];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await EmailSchema.MigrateOnceAsync(database, CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await DeleteOrganizationRowsAsync();
        foreach (var dataSource in _dataSources)
        {
            await dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClosePending_OlderThanAnHour_MarksOnlyThoseRowsUnknownAsync()
    {
        var stale = await AddAsync("pending", updatedAt: s_now.AddHours(-1).AddSeconds(-1));
        var fresh = await AddAsync("pending", updatedAt: s_now.AddMinutes(-59));
        var sent = await AddAsync("sent", updatedAt: s_now.AddDays(-2));
        var failed = await AddAsync("failed", updatedAt: s_now.AddDays(-2));

        var closed = await RunAsync(StaleJob(), CancellationToken);

        closed.Should().Be(1);
        var row = await RowAsync(stale);
        row.Status.Should().Be("unknown", "it is never closed as failed: a failed row would let a redelivery send again");
        row.UpdatedAt.Should().Be(s_now);
        (await RowAsync(fresh)).Status.Should().Be("pending");
        (await RowAsync(sent)).Status.Should().Be("sent");
        (await RowAsync(failed)).Status.Should().Be("failed");
        (await RunAsync(StaleJob(), CancellationToken)).Should().Be(0, "a second run finds nothing more");
    }

    [Fact]
    public async Task ClosePending_WithMoreDueThanABatch_StopsAtTheBatchOldestFirstAsync()
    {
        var rows = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            rows.Add(await AddAsync("pending", updatedAt: s_now.AddDays(-3).AddMinutes(index)));
        }

        var first = await RunAsync(StaleJob(batchSize: 2), CancellationToken, maxBatches: 2);

        first.Should().Be(4);
        (await RowAsync(rows[4])).Status.Should().Be("pending", "the newest waits for the next run");
        (await RunAsync(StaleJob(batchSize: 2), CancellationToken, maxBatches: 2)).Should().Be(1);
    }

    [Fact]
    public async Task Retention_DeletesOnlyRowsOlderThanNinetyDaysWhateverTheirStatusAsync()
    {
        var oldSent = await AddAsync("sent", createdAt: s_now.AddDays(-90).AddSeconds(-1));
        var oldFailed = await AddAsync("failed", createdAt: s_now.AddDays(-200));
        var oldUnknown = await AddAsync("unknown", createdAt: s_now.AddDays(-91));
        var young = await AddAsync("sent", createdAt: s_now.AddDays(-89));
        var recentPending = await AddAsync("pending", createdAt: s_now.AddDays(-1));

        var deleted = await RunAsync(RetentionJob(), CancellationToken);

        deleted.Should().Be(3);
        (await ExistsAsync(oldSent)).Should().BeFalse();
        (await ExistsAsync(oldFailed)).Should().BeFalse();
        (await ExistsAsync(oldUnknown)).Should().BeFalse();
        (await ExistsAsync(young)).Should().BeTrue();
        (await ExistsAsync(recentPending)).Should().BeTrue();
        (await RunAsync(RetentionJob(), CancellationToken)).Should().Be(0, "a second run does nothing");
    }

    [Fact]
    public async Task MeasureBacklog_CountsDueRowsAndReportsWhenTheOldestBecameDueAsync()
    {
        await AddAsync("pending", updatedAt: s_now.AddHours(-5));
        await AddAsync("pending", updatedAt: s_now.AddHours(-2));
        await AddAsync("pending", updatedAt: s_now.AddMinutes(-5));
        await AddAsync("sent", createdAt: s_now.AddDays(-100));
        await AddAsync("sent", createdAt: s_now.AddDays(-95));
        await AddAsync("sent", createdAt: s_now.AddDays(-10));
        await using var connection = await OpenMaintenanceAsync();

        var stale = await StaleJob().MeasureBacklogAsync(connection, CancellationToken);
        var retention = await RetentionJob().MeasureBacklogAsync(connection, CancellationToken);

        stale.Count.Should().Be(2);
        stale.OldestDue.Should().Be(s_now.AddHours(-5) + StaleDeliveryJob.StaleAfter);
        retention.Count.Should().Be(2);
        retention.OldestDue.Should().Be(s_now.AddDays(-100) + DeliveryRetentionJob.Retention);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Constant statements of this class.")]
    public async Task MaintenanceRole_CannotReadTheRecipientHashTheMessageIdOrTheReferenceAsync()
    {
        await using var connection = await OpenMaintenanceAsync();

        foreach (var sql in s_forbiddenReads)
        {
            var read = async () =>
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteScalarAsync(CancellationToken);
            };
            (await read.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        var filtered = async () =>
        {
            await using var command = new NpgsqlCommand("SELECT id FROM email.deliveries WHERE recipient_hash IS NULL LIMIT 1", connection);
            await command.ExecuteScalarAsync(CancellationToken);
        };
        (await filtered.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "filtering needs SELECT too");
    }

    [Fact]
    public async Task Status_Unknown_BlocksANewDeliveryForTheSameReferenceUntilItIsFailedAsync()
    {
        var reference = Guid.CreateVersion7();
        await AddAsync("unknown", reference: reference);

        var second = async () => await AddAsync("pending", reference: reference);

        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Redelivery_AfterAStalePendingRowWasClosed_StillSendsNothingAsync()
    {
        // The worker stopped after the hand-over: the delivery is pending and the event's inbox row was lost.
        var ready = new InvitationReady(Guid.CreateVersion7(), _organization, DateTimeOffset.UtcNow, Guid.CreateVersion7(), 1, InvitationReady.DefaultLocale);
        var address = EmailServices.NewAddress();
        var delivery = await AddAsync("pending", updatedAt: s_now.AddHours(-3), reference: ready.InvitationId, referenceVersion: ready.CodeVersion);

        await RunAsync(StaleJob(), CancellationToken);
        (await RowAsync(delivery)).Status.Should().Be("unknown");

        // Much later the broker delivers the event again, and there is no inbox record.
        var links = new FakeLinks(ready, address);
        using var provider = EmailServices.Build(
            "Development",
            options =>
            {
                options.Mailpit.Host = mailpit.SmtpHost;
                options.Mailpit.Port = mailpit.SmtpPort;
            },
            database.ApplicationConnectionString(),
            configureServices: services => services.AddSingleton<IInvitationLinks>(links));
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(ready.OrgId);
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginAsync(CancellationToken);
            await scope.ServiceProvider.GetRequiredService<InvitationReadyConsumer>().HandleAsync(ready, CancellationToken);
            await unitOfWork.CommitAsync(CancellationToken);
        }

        (await mailpit.MessagesToAsync(address, CancellationToken)).Should().BeEmpty("the first send may have arrived; a second invitation must not");
        (await RowAsync(delivery)).Status.Should().Be("unknown");
    }

    private StaleDeliveryJob StaleJob(int batchSize = StaleDeliveryJob.DefaultBatchSize) =>
        new(_time, NullLogger<StaleDeliveryJob>.Instance, batchSize);

    private DeliveryRetentionJob RetentionJob(int batchSize = DeliveryRetentionJob.DefaultBatchSize) =>
        new(_time, NullLogger<DeliveryRetentionJob>.Instance, batchSize);

    private async Task<NpgsqlConnection> OpenMaintenanceAsync()
    {
        var dataSource = NpgsqlDataSource.Create(database.MaintenanceConnectionString);
        _dataSources.Add(dataSource);
        return await dataSource.OpenConnectionAsync(CancellationToken);
    }

    // What the runner does for one run: batches, each in its own transaction, until one is short or the limit is reached.
    private async Task<int> RunAsync(IMaintenanceJob job, CancellationToken cancellationToken, int maxBatches = 20)
    {
        await using var connection = await OpenMaintenanceAsync();
        var total = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var handled = await job.RunBatchAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            total += handled;
            if (handled < job.BatchSize)
            {
                break;
            }
        }

        return total;
    }

    private async Task<Guid> AddAsync(string status, DateTimeOffset? createdAt = null, DateTimeOffset? updatedAt = null, Guid? reference = null, int? referenceVersion = null)
    {
        var id = Guid.CreateVersion7();
        var created = createdAt ?? updatedAt ?? s_now;
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO email.deliveries (id, org_id, template, template_version, recipient_hash, status, created_at, updated_at, reference_id, reference_version)
            VALUES ($1, $2, 'invitation', 1, $3, $4, $5, $6, $7, $8)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _organization });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = RandomNumberGenerator.GetBytes(32) });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = created });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = updatedAt ?? created });
        command.Parameters.Add(new NpgsqlParameter { Value = reference is { } r ? r : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = reference is null ? DBNull.Value : referenceVersion ?? 1, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        await command.ExecuteNonQueryAsync(CancellationToken);
        return id;
    }

    private async Task<(string Status, DateTimeOffset UpdatedAt)> RowAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status, updated_at FROM email.deliveries WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        (await reader.ReadAsync(CancellationToken)).Should().BeTrue();
        return (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM email.deliveries WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return (long)(await command.ExecuteScalarAsync(CancellationToken))! == 1;
    }

    private async Task DeleteOrganizationRowsAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM email.deliveries WHERE org_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _organization });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private sealed class FakeLinks(InvitationReady ready, string address) : IInvitationLinks
    {
        private readonly ConcurrentDictionary<(Guid, int), InvitationLink> _links = new()
        {
            [(ready.InvitationId, ready.CodeVersion)] = new InvitationLink(new Uri("https://app.backroom.test/invite#" + new string('a', 43)), address, "Acme"),
        };

        public Task<string?> GetCodeHashAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InvitationLink?> GetLinkAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken) =>
            Task.FromResult(_links.TryGetValue((invitationId, codeVersion), out var link) ? link : null);
    }
}
