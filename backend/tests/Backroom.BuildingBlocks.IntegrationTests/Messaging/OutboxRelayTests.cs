using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;
using Backroom.Testing.Postgres;
using Backroom.Testing.RabbitMq;
using RabbitMQ.Client;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

[Collection(PostgresCollection.Name)]
public sealed class OutboxRelayTests(PostgresDatabase database, RabbitMqBroker broker) : IAsyncLifetime
{
    private const string Exchange = "rls_test.events";

    private static readonly Guid s_organization = Guid.CreateVersion7();

    private readonly ServiceProvider _services = NotesSchema.CreateServices(database);
    private readonly List<IAsyncDisposable> _disposables = [];
    private IConnection _consumerConnection = null!;
    private IChannel _consumer = null!;
    private string _queue = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await NotesSchema.PrepareAsync(database);

        // A private queue that receives every event of the test module.
        _consumerConnection = await new ConnectionFactory { Uri = new Uri(broker.ConnectionString) }.CreateConnectionAsync(CancellationToken);
        _consumer = await _consumerConnection.CreateChannelAsync(cancellationToken: CancellationToken);
        await RabbitMqTopology.DeclareEventExchangeAsync(_consumer, NotesSchema.Schema, CancellationToken);
        _queue = (await _consumer.QueueDeclareAsync(cancellationToken: CancellationToken)).QueueName;
        await _consumer.QueueBindAsync(_queue, Exchange, "#", cancellationToken: CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _consumer.DisposeAsync();
        await _consumerConnection.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task RelayBatch_WhenEventsAreCommitted_PublishesAndMarksThemAsync()
    {
        using var activity = new Activity("test request").Start();
        var events = await AddCommittedAsync(3);
        using var relay = CreateRelay();

        var published = await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);
        var messages = await ReceiveAsync(3);

        published.Should().Be(3);
        messages.Select(m => m.BasicProperties.MessageId).Should().BeEquivalentTo(events.Select(e => e.EventId.ToString()));
        var first = messages.Single(m => m.BasicProperties.MessageId == events[0].EventId.ToString());
        first.RoutingKey.Should().Be(NoteAdded.EventType);
        first.BasicProperties.Type.Should().Be(NoteAdded.EventType);
        first.BasicProperties.Persistent.Should().BeTrue();
        Header(first, MessagingTelemetry.OrganizationHeader).Should().Be(s_organization.ToString());
        Header(first, MessagingTelemetry.TraceParentHeader).Should().Contain(activity.TraceId.ToHexString());
        IntegrationEventSerializer.Deserialize<NoteAdded>(first.Body.Span).Should().Be(events[0]);

        (await OutboxRows.ReadAsync(database, CancellationToken)).Should().OnlyContain(row => row.PublishedAt != null);
        (await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task RelayBatch_WhenUnitOfWorkIsStillOpen_PublishesNothingAsync()
    {
        await using var scope = await BeginAsync();
        await scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(NoteAdded.For(s_organization), CancellationToken);
        using var relay = CreateRelay();

        var published = await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);

        published.Should().Be(0);
    }

    [Fact]
    public async Task RelayBatch_FromTwoRelaysAtOnce_PublishesEachEventOnceAsync()
    {
        await AddCommittedAsync(60);
        using var first = CreateRelay(batchSize: 10);
        using var second = CreateRelay(batchSize: 10);

        await Task.WhenAll(DrainAsync(first), DrainAsync(second));
        var messages = await ReceiveAsync(60);

        messages.Select(m => m.BasicProperties.MessageId).Should().OnlyHaveUniqueItems().And.HaveCount(60);
        (await _consumer.MessageCountAsync(_queue, CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task RelayBatch_WhenBrokerIsUnreachable_LeavesEventsUnpublishedAsync()
    {
        await AddCommittedAsync(2);
        using var relay = CreateRelay(rabbitMqConnectionString: "amqp://guest:guest@127.0.0.1:1/");

        var relayBatch = () => relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);

        await relayBatch.Should().ThrowAsync<Exception>();
        (await OutboxRows.ReadAsync(database, CancellationToken)).Should().HaveCount(2).And.OnlyContain(row => row.PublishedAt == null);
    }

    [Fact]
    public async Task RelayBatch_WithRoleWithoutBypassRls_ThrowsAsync()
    {
        using var relay = CreateRelay(databaseConnectionString: database.ApplicationConnectionString());

        var relayBatch = () => relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);

        await relayBatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BYPASSRLS*");
    }

    [Fact]
    public async Task Cleanup_WhenRetentionHasPassed_DeletesOnlyOldPublishedEventsAsync()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var relay = CreateRelay(timeProvider: time);
        await AddCommittedAsync(2);
        await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);
        time.Advance(TimeSpan.FromDays(2));
        var recent = await AddCommittedAsync(1);
        await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);
        var unpublished = await AddCommittedAsync(1);

        var deleted = await relay.CleanupAsync(CancellationToken);

        deleted.Should().Be(2);
        (await OutboxRows.ReadAsync(database, CancellationToken)).Select(row => row.Id)
            .Should().BeEquivalentTo([recent[0].EventId, unpublished[0].EventId]);
    }

    [Fact]
    public async Task Relay_WhenRunningAsHostedService_PublishesNewEventsAsync()
    {
        using var relay = CreateRelay();
        await relay.StartAsync(CancellationToken);
        try
        {
            var events = await AddCommittedAsync(1);

            var messages = await ReceiveAsync(1);

            messages.Should().ContainSingle().Which.BasicProperties.MessageId.Should().Be(events[0].EventId.ToString());
        }
        finally
        {
            await relay.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task RelayBatch_WhenRowsCannotBePublished_MarksThemFailedAndPublishesTheRestAsync()
    {
        var invalidType = Guid.CreateVersion7();
        var otherModule = Guid.CreateVersion7();
        await database.ExecuteAsSuperuserAsync($"""
            INSERT INTO rls_test.outbox (id, org_id, event_type, payload, occurred_at) VALUES
                ('{invalidType}', '{s_organization}', 'BAD TYPE', '\x7b7d', now()),
                ('{otherModule}', '{s_organization}', 'messaging.message-posted.v1', '\x7b7d', now());
            """);
        var events = await AddCommittedAsync(2);
        using var relay = CreateRelay();

        await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);
        var messages = await ReceiveAsync(2);

        messages.Select(m => m.BasicProperties.MessageId).Should().BeEquivalentTo(events.Select(e => e.EventId.ToString()));
        var rows = await OutboxRows.ReadAsync(database, CancellationToken);
        rows.Where(row => row.FailedAt != null).Select(row => row.Id).Should().BeEquivalentTo([invalidType, otherModule]);
        (await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task RelayOnce_WhenOneSchemaFails_StillPublishesTheOthersAsync()
    {
        await AddCommittedAsync(1);
        using var relay = CreateRelay(schemas: ["missing_module", NotesSchema.Schema]);

        await relay.RelayOnceAsync(CancellationToken);

        (await ReceiveAsync(1)).Should().ContainSingle();
    }

    [Fact]
    public async Task RelayBatch_WhenNoQueueIsBound_KeepsTheEventInTheUnroutedQueueAsync()
    {
        var unrouted = RabbitMqTopology.UnroutedName(Exchange);
        await _consumer.QueueUnbindAsync(_queue, Exchange, "#", cancellationToken: CancellationToken);
        await _consumer.QueuePurgeAsync(unrouted, CancellationToken);
        var events = await AddCommittedAsync(1);
        using var relay = CreateRelay();

        await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);

        var kept = await _consumer.BasicGetAsync(unrouted, autoAck: true, CancellationToken);
        kept.Should().NotBeNull();
        kept!.BasicProperties.MessageId.Should().Be(events[0].EventId.ToString());
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("member-of-owner")]
    public async Task RelayBatch_WithRoleThatOwnsOrIsSuperuser_ThrowsAsync(string kind)
    {
        var connectionString = kind == "superuser"
            ? database.SuperuserConnectionString
            : await database.CreateLoginRoleAsync($"relay_owner_{Guid.CreateVersion7().ToString("N")[^12..]}", "BYPASSRLS", PostgresDatabase.MigratorRole);
        using var relay = CreateRelay(databaseConnectionString: connectionString);

        var relayBatch = () => relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);

        await relayBatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BYPASSRLS*");
    }

    [Theory]
    [Trait("Category", "Privacy")]
    [InlineData("SELECT count(*) FROM rls_test.notes")]
    [InlineData("INSERT INTO rls_test.outbox (id, org_id, event_type, payload, occurred_at) VALUES (gen_random_uuid(), gen_random_uuid(), 'rls_test.forged.v1', '\\x7b7d', now())")]
    [InlineData("UPDATE rls_test.outbox SET payload = '\\x7b7d'")]
    [InlineData("TRUNCATE rls_test.outbox")]
    public async Task RelayRole_OutsideItsOutboxPrivileges_IsRejectedAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.RelayConnectionString);
        await connection.OpenAsync(CancellationToken);
#pragma warning disable CA2100 // Test SQL from InlineData.
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
#pragma warning restore CA2100

        var run = () => command.ExecuteNonQueryAsync(CancellationToken);

        (await run.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    private static string? Header(BasicGetResult message, string name) =>
        message.BasicProperties.Headers?.TryGetValue(name, out var value) == true && value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private static async Task DrainAsync(OutboxRelay relay)
    {
        while (await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken) > 0)
        {
        }
    }

    private OutboxRelay CreateRelay(
        int batchSize = 100,
        string? rabbitMqConnectionString = null,
        string? databaseConnectionString = null,
        TimeProvider? timeProvider = null,
        string[]? schemas = null)
    {
        var relayOptions = new OutboxRelayOptions
        {
            ConnectionString = databaseConnectionString ?? database.RelayConnectionString,
            BatchSize = batchSize,
            PollInterval = TimeSpan.FromMilliseconds(50),
        };
        foreach (var schema in schemas ?? [NotesSchema.Schema])
        {
            relayOptions.Schemas.Add(schema);
        }

        var connection = new RabbitMqConnection(Options.Create(new RabbitMqOptions
        {
            ConnectionString = rabbitMqConnectionString ?? broker.ConnectionString,
            ClientName = "outbox-relay-tests",
        }));
        _disposables.Add(connection);

        return new OutboxRelay(Options.Create(relayOptions), connection, timeProvider ?? TimeProvider.System, NullLoggerFactory.Instance);
    }

    private async Task<List<NoteAdded>> AddCommittedAsync(int count)
    {
        var events = Enumerable.Range(0, count).Select(_ => NoteAdded.For(s_organization)).ToList();
        await using var scope = await BeginAsync();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        foreach (var noteAdded in events)
        {
            await outbox.AddAsync(noteAdded, CancellationToken);
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken);
        return events;
    }

    private async Task<AsyncServiceScope> BeginAsync()
    {
        var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(s_organization);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(CancellationToken);
        return scope;
    }

    private async Task<List<BasicGetResult>> ReceiveAsync(int expected)
    {
        var messages = new List<BasicGetResult>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (messages.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            if (await _consumer.BasicGetAsync(_queue, autoAck: true, CancellationToken) is { } message)
            {
                messages.Add(message);
            }
            else
            {
                await Task.Delay(20, CancellationToken);
            }
        }

        return messages;
    }
}
