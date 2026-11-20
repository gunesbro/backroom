using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;
using Backroom.Testing.Postgres;
using Backroom.Testing.RabbitMq;
using RabbitMQ.Client;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

[Collection(PostgresCollection.Name)]
public sealed class IntegrationEventConsumerTests(PostgresDatabase database, RabbitMqBroker broker) : IAsyncLifetime
{
    private const string Queue = "rls_test.rls_test.note-added.v1";
    private const string DeadLetterQueue = Queue + ".dlq";
    private const string Exchange = "rls_test.events";

    private static readonly Guid s_organization = Guid.CreateVersion7();
    private static readonly TimeSpan[] s_retryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    private ServiceProvider _services = null!;
    private IntegrationEventConsumerHost _host = null!;
    private HandlerProbe _probe = null!;
    private IConnection _testConnection = null!;
    private IChannel _testChannel = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await NotesSchema.PrepareAsync(database);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDatabaseSession().Configure(options => options.ConnectionString = database.ApplicationConnectionString());
        services.AddModuleDbContext<NotesDbContext>(NotesSchema.Schema);
        services.AddOutbox();
        services.AddRabbitMq().Configure(options =>
        {
            options.ConnectionString = broker.ConnectionString;
            options.ClientName = "consumer-tests";
        });
        services.AddSingleton<HandlerProbe>();
        services.AddIntegrationEventHandler<NoteAdded, NoteAddedHandler>(NotesSchema.Schema);
        services.AddIntegrationEventConsumers().Configure(options =>
        {
            options.RetryDelays.Clear();
            foreach (var delay in s_retryDelays)
            {
                options.RetryDelays.Add(delay);
            }

            options.RequeuePause = TimeSpan.FromMilliseconds(10);
        });
        _services = services.BuildServiceProvider();
        _probe = _services.GetRequiredService<HandlerProbe>();
        _host = _services.GetServices<IHostedService>().OfType<IntegrationEventConsumerHost>().Single();

        _testConnection = await new ConnectionFactory { Uri = new Uri(broker.ConnectionString) }.CreateConnectionAsync(CancellationToken);
        _testChannel = await _testConnection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), CancellationToken);

        await _host.StartAsync(CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);

        // Delete the queues, not only purge them: a durable queue left bound to the module's exchange would
        // receive the events other test classes expect elsewhere (OutboxRelayTests' unrouted queue). The next
        // host declares them again. Deleting a missing queue is a no-op. A channel of its own, because a test
        // may have closed _testChannel.
        await using (var channel = await _testConnection.CreateChannelAsync(cancellationToken: CancellationToken.None))
        {
            foreach (var queue in new[] { Queue, DeadLetterQueue }.Concat(s_retryDelays.Select(RetryQueue)))
            {
                await channel.QueueDeleteAsync(queue, cancellationToken: CancellationToken.None);
            }
        }

        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _testChannel.DisposeAsync();
        await _testConnection.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Start_WhenReturned_HasDeclaredTheQueuesAsync()
    {
        foreach (var queue in new[] { Queue, DeadLetterQueue }.Concat(s_retryDelays.Select(RetryQueue)))
        {
            var declare = () => _testChannel.QueueDeclarePassiveAsync(queue, CancellationToken);
            await declare.Should().NotThrowAsync();
        }
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Event_WhenDelivered_IsHandledOnceInItsOrganizationAsync()
    {
        var noteAdded = NoteAdded.For(s_organization);

        await PublishAsync(noteAdded);
        await WaitUntilAsync(async () => await CountAsync("notes", "id", noteAdded.NoteId) == 1);

        _probe.Calls.Should().ContainSingle().Which.OrganizationId.Should().Be(s_organization);
        (await CountAsync("inbox", "event_id", noteAdded.EventId)).Should().Be(1);
    }

    [Fact]
    public async Task Event_WhenDeliveredTwice_IsHandledOnceAsync()
    {
        var duplicate = NoteAdded.For(s_organization);
        var sentinel = NoteAdded.For(s_organization);

        await PublishAsync(duplicate);
        await PublishAsync(duplicate);
        await PublishAsync(sentinel);

        // One delivery at a time per queue, in order: once the sentinel is handled, both duplicates were.
        await WaitUntilAsync(async () => await CountAsync("notes", "id", sentinel.NoteId) == 1);
        _probe.CallsFor(duplicate.EventId).Should().Be(1);
    }

    [Fact]
    public async Task Handler_WhenItFailsOnce_IsRetriedAndItsFirstAttemptRolledBackAsync()
    {
        var noteAdded = NoteAdded.For(s_organization);
        _probe.FailTimes(noteAdded.EventId, 1);

        await PublishAsync(noteAdded);
        await WaitUntilAsync(async () => await CountAsync("notes", "id", noteAdded.NoteId) == 1);

        // The second attempt inserted the same note id, so the first attempt's insert was rolled back.
        _probe.CallsFor(noteAdded.EventId).Should().Be(2);
    }

    [Fact]
    public async Task Handler_WhenItKeepsFailing_MovesTheEventToTheDeadLetterQueueAsync()
    {
        var noteAdded = NoteAdded.For(s_organization);
        _probe.FailTimes(noteAdded.EventId, 10);

        await PublishAsync(noteAdded);
        var deadLetter = await ReceiveDeadLetterAsync();

        deadLetter.BasicProperties.MessageId.Should().Be(noteAdded.EventId.ToString());
        deadLetter.BasicProperties.Headers![IntegrationEventConsumerHost.AttemptHeader].Should().Be(s_retryDelays.Length + 1);
        Encoding.UTF8.GetString((byte[])deadLetter.BasicProperties.Headers[IntegrationEventConsumerHost.FailureHeader]!)
            .Should().Be(nameof(InvalidOperationException));
        _probe.CallsFor(noteAdded.EventId).Should().Be(s_retryDelays.Length + 1);
        (await CountAsync("notes", "id", noteAdded.NoteId)).Should().Be(0);
    }

    [Fact]
    public async Task Message_WithInvalidPayload_MovesToTheDeadLetterQueueWithoutHandlingAsync()
    {
        var noteAdded = NoteAdded.For(s_organization);

        await PublishAsync(noteAdded, body: Encoding.UTF8.GetBytes("not json"));
        var deadLetter = await ReceiveDeadLetterAsync();

        Encoding.UTF8.GetString((byte[])deadLetter.BasicProperties.Headers![IntegrationEventConsumerHost.FailureHeader]!)
            .Should().Be("invalid payload");
        _probe.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Message_WhoseOrganizationHeaderDiffersFromThePayload_IsNotHandledAsync()
    {
        var noteAdded = NoteAdded.For(s_organization);

        await PublishAsync(noteAdded, organizationHeader: Guid.CreateVersion7());
        var deadLetter = await ReceiveDeadLetterAsync();

        Encoding.UTF8.GetString((byte[])deadLetter.BasicProperties.Headers![IntegrationEventConsumerHost.FailureHeader]!)
            .Should().Be("payload does not match envelope");
        _probe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Republish_WhenTheRetryQueueIsMissing_EndsInTheDeadLetterQueueAsync()
    {
        // Regression: a failed republish requeued the delivery without limit.
        var noteAdded = NoteAdded.For(s_organization);
        _probe.FailTimes(noteAdded.EventId, 100);
        await _testChannel.QueueDeleteAsync(RetryQueue(s_retryDelays[0]), cancellationToken: CancellationToken);

        await PublishAsync(noteAdded);
        var deadLetter = await ReceiveDeadLetterAsync();

        deadLetter.BasicProperties.MessageId.Should().Be(noteAdded.EventId.ToString());
        _probe.CallsFor(noteAdded.EventId).Should().Be(IntegrationEventConsumerHost.MaxRequeues + 1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("type")]
    public async Task Message_WithInvalidEnvelope_MovesToTheDeadLetterQueueWithoutHandlingAsync(string defect)
    {
        var noteAdded = defect == "empty"
            ? NoteAdded.For(Guid.Empty)
            : NoteAdded.For(s_organization);

        await PublishAsync(
            noteAdded,
            organizationHeader: defect == "empty" ? Guid.Empty : null,
            omitOrganizationHeader: defect == "missing",
            type: defect == "type" ? "rls_test.other-event.v1" : null);
        var deadLetter = await ReceiveDeadLetterAsync();

        Encoding.UTF8.GetString((byte[])deadLetter.BasicProperties.Headers![IntegrationEventConsumerHost.FailureHeader]!)
            .Should().Be("invalid envelope");
        _probe.Calls.Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Privacy")]
    [InlineData("SELECT count(*) FROM rls_test.inbox")]
    [InlineData("DELETE FROM rls_test.inbox")]
    [InlineData("UPDATE rls_test.inbox SET handler = 'x'")]
    public async Task ApplicationRole_ReadingOrChangingTheInbox_IsRejectedAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ApplicationConnectionString());
        await connection.OpenAsync(CancellationToken);
#pragma warning disable CA2100 // Test SQL from InlineData.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100

        var run = () => command.ExecuteNonQueryAsync(CancellationToken);

        (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public void RelayOptions_WhenAHandlerIsRegistered_IncludeItsInboxSchema()
    {
        _services.GetRequiredService<IOptions<OutboxRelayOptions>>().Value.InboxSchemas.Should().Contain(NotesSchema.Schema);
    }

    [Fact]
    public async Task Event_FromOutboxThroughRelay_IsHandledInTheSameTraceAsync()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MessagingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var request = new Activity("test request").Start();
        var noteAdded = NoteAdded.For(s_organization);

        await using (var scope = _services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(s_organization);
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginAsync(CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(noteAdded, CancellationToken);
            await unitOfWork.CommitAsync(CancellationToken);
        }

        using var relay = CreateRelay(TimeProvider.System);
        await relay.RelayBatchAsync(NotesSchema.Schema, CancellationToken);
        await WaitUntilAsync(async () => await CountAsync("notes", "id", noteAdded.NoteId) == 1);

        _probe.Calls.Should().ContainSingle().Which.TraceId.Should().Be(request.TraceId);
    }

    [Fact]
    public async Task Cleanup_WhenInboxRetentionHasPassed_DeletesOnlyOldRowsAsync()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var old = Guid.CreateVersion7();
        var recent = Guid.CreateVersion7();
        await database.ExecuteAsSuperuserAsync($"""
            INSERT INTO rls_test.inbox (event_id, handler, org_id, processed_at) VALUES
                ('{old}', 'test', '{s_organization}', now() - interval '20 days'),
                ('{recent}', 'test', '{s_organization}', now() - interval '1 day');
            """);
        using var relay = CreateRelay(time);

        await relay.CleanupAsync(CancellationToken);

        (await CountAsync("inbox", "event_id", old)).Should().Be(0);
        (await CountAsync("inbox", "event_id", recent)).Should().Be(1);
    }

    private static string RetryQueue(TimeSpan delay) => $"{Queue}.retry.{(long)delay.TotalMilliseconds}ms";

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(25, CancellationToken);
        }
    }

    private OutboxRelay CreateRelay(TimeProvider timeProvider)
    {
        var options = new OutboxRelayOptions { ConnectionString = database.RelayConnectionString };
        options.Schemas.Add(NotesSchema.Schema);
        options.InboxSchemas.Add(NotesSchema.Schema);
        var connection = new RabbitMqConnection(Options.Create(new RabbitMqOptions { ConnectionString = broker.ConnectionString }));
        _disposables.Add(connection);
        return new OutboxRelay(Options.Create(options), connection, timeProvider, NullLoggerFactory.Instance);
    }

    private async Task PublishAsync(
        NoteAdded noteAdded,
        Guid? organizationHeader = null,
        byte[]? body = null,
        bool omitOrganizationHeader = false,
        string? type = null)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!omitOrganizationHeader)
        {
            headers[MessagingTelemetry.OrganizationHeader] = (organizationHeader ?? noteAdded.OrgId).ToString();
        }

        var properties = new BasicProperties
        {
            MessageId = noteAdded.EventId.ToString(),
            Type = type ?? NoteAdded.EventType,
            ContentType = IntegrationEventSerializer.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };
        await _testChannel.BasicPublishAsync(
            Exchange, NoteAdded.EventType, mandatory: true, properties, body ?? IntegrationEventSerializer.Serialize(noteAdded), CancellationToken);
    }

    private async Task<BasicGetResult> ReceiveDeadLetterAsync()
    {
        BasicGetResult? message = null;
        await WaitUntilAsync(async () => (message = await _testChannel.BasicGetAsync(DeadLetterQueue, autoAck: true, CancellationToken)) is not null);
        return message!;
    }

    private async Task<long> CountAsync(string table, string column, Guid id)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
#pragma warning disable CA2100 // Table and column names are constants of this test.
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM rls_test.{table} WHERE {column} = $1", connection);
#pragma warning restore CA2100
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
