using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Consumes integration events for the registered handlers (docs/adr/0006). On start it declares
/// every queue, binding, retry queue and dead-letter queue before consuming, so events published
/// afterwards reach their queues. Each delivery is handled in a unit of work in the event's
/// organization, recorded in the consumer's inbox in the same transaction (a redelivery is skipped),
/// and acknowledged only after the commit. A failed delivery is republished to the next retry queue,
/// which returns it to the main queue after its delay; after the last retry, or at once when the
/// message is malformed, it goes to the dead-letter queue. Transient infrastructure failures (the
/// database is unreachable, a deadlock) do not use up retries: the delivery is requeued after a
/// growing pause.
/// </summary>
public sealed partial class IntegrationEventConsumerHost : BackgroundService
{
    /// <summary>How many times the delivery has failed so far.</summary>
    public const string AttemptHeader = "x-backroom-attempt";

    /// <summary>
    /// Why a message was dead-lettered: a fixed reason, or the exception type with PostgreSQL's error
    /// code, constraint and table when the database raised it; never the exception message. The
    /// <c>traceparent</c> header leads to the trace and the logged stack trace.
    /// </summary>
    public const string FailureHeader = "x-backroom-failure";

    /// <summary>When the delivery last failed, in Unix milliseconds.</summary>
    public const string FailedAtHeader = "x-backroom-failed-at";

    /// <summary>Deliveries of a quorum queue before the broker dead-letters the message (set explicitly).</summary>
    public const int DeliveryLimit = 20;

    /// <summary>Failed acknowledgements or republishes of one message before it is rejected to the dead-letter queue.</summary>
    internal const int MaxRequeues = 5;

    private const int MaxTrackedMessages = 10_000;

    private static readonly TimeSpan s_maxPause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_confirmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_shutdownPoll = TimeSpan.FromMilliseconds(50);

    private readonly IReadOnlyList<IntegrationEventSubscription> _subscriptions;
    private readonly RabbitMqConnection _rabbitMq;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IntegrationEventConsumerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IntegrationEventConsumerHost> _logger;
    private readonly List<(IChannel Channel, string ConsumerTag)> _consumers = [];
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    // Per message (queue and message id): failed acknowledgements/republishes, and transient failures.
    private readonly ConcurrentDictionary<string, int> _requeueFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _transientFailures = new(StringComparer.Ordinal);
    private IChannel? _publishChannel;
    private int _inFlight;
    private volatile bool _stopRequested;

    internal IntegrationEventConsumerHost(
        IEnumerable<IntegrationEventSubscription> subscriptions,
        RabbitMqConnection rabbitMq,
        IServiceScopeFactory scopeFactory,
        IOptions<IntegrationEventConsumerOptions> options,
        TimeProvider timeProvider,
        ILogger<IntegrationEventConsumerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _subscriptions = subscriptions.ToList();
        _rabbitMq = rabbitMq;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        var duplicate = _subscriptions.GroupBy(s => s.Queue, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"More than one handler is registered for queue {duplicate.Key}.");
        }
    }

    private enum OutcomeKind
    {
        Ack,
        Requeue,
        RequeueTransient,
        Republish,
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var connection = await _rabbitMq.GetAsync(cancellationToken);

        await using (var topology = await connection.CreateChannelAsync(cancellationToken: cancellationToken))
        {
            foreach (var subscription in _subscriptions)
            {
                await DeclareAsync(topology, subscription, cancellationToken);
            }
        }

        _publishChannel = await CreatePublishChannelAsync(connection, cancellationToken);

        foreach (var subscription in _subscriptions)
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await channel.BasicQosAsync(prefetchSize: 0, _options.Prefetch, global: false, cancellationToken);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(subscription, channel, delivery);
            consumer.ShutdownAsync += (_, _) => OnConsumerStoppedAsync(subscription);
            consumer.UnregisteredAsync += (_, _) => OnConsumerStoppedAsync(subscription);
            var consumerTag = await channel.BasicConsumeAsync(subscription.Queue, autoAck: false, consumer, cancellationToken);
            _consumers.Add((channel, consumerTag));
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop new deliveries and let the ones already dispatched finish; only when the host's
        // shutdown timeout runs out are they cancelled (and requeued by the broker).
        _stopRequested = true;
        foreach (var (channel, consumerTag) in _consumers)
        {
            if (channel.IsOpen)
            {
                await channel.BasicCancelAsync(consumerTag, cancellationToken: cancellationToken);
            }
        }

        await WaitForDeliveriesAsync(cancellationToken);
        await _stopping.CancelAsync();
        using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider))
        {
            await WaitForDeliveriesAsync(grace.Token);
        }

        foreach (var (channel, _) in _consumers)
        {
            await channel.DisposeAsync();
        }

        _consumers.Clear();
        if (_publishChannel is not null)
        {
            await _publishChannel.DisposeAsync();
            _publishChannel = null;
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _publishLock.Dispose();
        _stopping.Dispose();
        base.Dispose();
    }

    /// <summary>True for failures that say nothing about the event: the database is unreachable, a deadlock, a timeout.</summary>
    internal static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The exception type and, if PostgreSQL raised it, its error code, constraint and table: enough
    /// to recognise most failures, without the message (which may contain personal data).
    /// </summary>
    internal static string DescribeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var description = new StringBuilder(exception.GetType().Name);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                description.Append(" (SQLSTATE ").Append(postgres.SqlState);
                if (postgres.ConstraintName is { } constraint)
                {
                    description.Append(", constraint ").Append(constraint);
                }

                if (postgres.TableName is { } table)
                {
                    description.Append(", table ").Append(postgres.SchemaName).Append('.').Append(table);
                }

                description.Append(')');
                break;
            }
        }

        return description.ToString();
    }

    /// <summary>The type and stack trace of each exception in the chain, without messages.</summary>
    internal static string StackTraceWithoutMessages(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (text.Length > 0)
            {
                text.AppendLine().Append("--- caused by ");
            }

            text.Append(current.GetType().FullName).AppendLine().Append(current.StackTrace);
        }

        return text.ToString();
    }

    // Consuming is driven by the client's delivery callbacks started in StartAsync.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private static async Task DeclareAsync(IChannel channel, IntegrationEventSubscription subscription, CancellationToken cancellationToken)
    {
        await RabbitMqTopology.DeclareEventExchangeAsync(channel, subscription.PublisherModule, cancellationToken);

        // Bounded: dead letters need attention, and payloads may hold personal data the contract
        // allows (an invited email address), which must not be kept indefinitely.
        await channel.QueueDeclareAsync(
            subscription.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
                ["x-max-length"] = RabbitMqTopology.DeadLetterMaxLength,
                ["x-message-ttl"] = (long)RabbitMqTopology.DeadLetterRetention.TotalMilliseconds,
            },
            cancellationToken: cancellationToken);

        // A delivery that keeps crashing the consumer reaches the delivery limit and is dead-lettered
        // (at least once, which requires reject-publish) instead of dropped.
        await channel.QueueDeclareAsync(
            subscription.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = DeliveryLimit,
                ["x-overflow"] = "reject-publish",
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = subscription.DeadLetterQueue,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            subscription.Queue, IntegrationEventTypes.ExchangeFor(subscription.PublisherModule), subscription.EventType,
            cancellationToken: cancellationToken);
    }

    private async Task DeclareRetryQueuesAsync(IChannel channel, IntegrationEventSubscription subscription, CancellationToken cancellationToken)
    {
        // Replicated, and returned to the main queue at least once when the delay expires.
        foreach (var delay in _options.RetryDelays)
        {
            await channel.QueueDeclareAsync(
                subscription.RetryQueue(delay), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-queue-type"] = "quorum",
                    ["x-message-ttl"] = (long)delay.TotalMilliseconds,
                    ["x-overflow"] = "reject-publish",
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = subscription.Queue,
                },
                cancellationToken: cancellationToken);
        }
    }

    private async Task<IChannel> CreatePublishChannelAsync(IConnection connection, CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        foreach (var subscription in _subscriptions)
        {
            await DeclareRetryQueuesAsync(channel, subscription, cancellationToken);
        }

        return channel;
    }

    private Task OnConsumerStoppedAsync(IntegrationEventSubscription subscription)
    {
        if (!_stopRequested)
        {
            LogConsumerStopped(_logger, subscription.Queue);
        }

        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031", Justification = "A delivery callback must not throw; failures are logged and the delivery is requeued or rejected.")]
    private async Task OnDeliveryAsync(IntegrationEventSubscription subscription, IChannel channel, BasicDeliverEventArgs delivery)
    {
        Interlocked.Increment(ref _inFlight);
        var key = subscription.Queue + "|" + delivery.BasicProperties.MessageId;
        try
        {
            var outcome = await ProcessAsync(subscription, delivery, _stopping.Token);
            try
            {
                switch (outcome.Kind)
                {
                    case OutcomeKind.Republish:
                        await RepublishAsync(delivery, outcome.Queue!, outcome.Failures, outcome.Failure);
                        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
                        Forget(key);
                        break;
                    case OutcomeKind.RequeueTransient:
                        await PauseAsync(Track(_transientFailures, key));
                        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
                        break;
                    case OutcomeKind.Requeue:
                        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
                        break;
                    default:
                        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
                        Forget(key);
                        break;
                }
            }
            catch (Exception exception)
            {
                // Republishing or acknowledging failed (for example a retry queue was deleted). A nack
                // with requeue does not count towards the delivery limit, so the attempts are counted
                // here and the message is finally rejected to the dead-letter queue.
                LogDeliveryFailed(_logger, exception, subscription.Queue);
                if (!channel.IsOpen)
                {
                    return;
                }

                var failures = Track(_requeueFailures, key);
                if (failures > MaxRequeues)
                {
                    Forget(key);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None);
                }
                else
                {
                    await PauseAsync(failures);
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
                }
            }
        }
        catch (Exception exception)
        {
            // The channel is gone; the broker requeues the delivery.
            LogDeliveryFailed(_logger, exception, subscription.Queue);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Any handler failure is retried or dead-lettered; it must not stop the consumer.")]
    private async Task<Outcome> ProcessAsync(IntegrationEventSubscription subscription, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        var properties = delivery.BasicProperties;
        if (!Guid.TryParse(properties.MessageId, out var eventId)
            || !Guid.TryParse(HeaderString(properties, MessagingTelemetry.OrganizationHeader), out var organizationId)
            || organizationId == Guid.Empty
            || !string.Equals(properties.Type, subscription.EventType, StringComparison.Ordinal))
        {
            return DeadLetter(subscription, properties.MessageId, "invalid envelope");
        }

        ReceivedEvent? received;
        try
        {
            received = subscription.Deserialize(delivery.Body);
        }
        catch (JsonException)
        {
            return DeadLetter(subscription, properties.MessageId, "invalid payload");
        }

        // The organization the handler runs in must be the event's own.
        if (received is null || received.EventId != eventId || received.OrgId != organizationId)
        {
            return DeadLetter(subscription, properties.MessageId, "payload does not match envelope");
        }

        var previousFailures = ReadAttempt(properties);
        using var activity = StartProcessActivity(subscription, properties, eventId);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
            var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();
            await session.BeginAsync(cancellationToken);

            if (!await RecordInInboxAsync(session, subscription, eventId, organizationId, cancellationToken))
            {
                // Already handled: a redelivery after a lost acknowledgement, or a duplicate publish.
                await session.RollbackAsync(cancellationToken);
                return new Outcome(OutcomeKind.Ack);
            }

            await subscription.Handle(scope.ServiceProvider, received.Event, cancellationToken);
            await session.CommitAsync(cancellationToken);
            return new Outcome(OutcomeKind.Ack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Outcome(OutcomeKind.Requeue);
        }
        catch (Exception exception)
        {
            var exceptionType = exception.GetType().Name;
            var failure = DescribeFailure(exception);
            var stackTrace = StackTraceWithoutMessages(exception);

            // Exception messages may contain personal data, so above Debug only the type, database
            // error codes and the stack trace (code locations) are recorded; the trace id links them.
            activity?.SetStatus(ActivityStatusCode.Error, exceptionType);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName,
                ["exception.stacktrace"] = stackTrace,
            }));
            LogHandlingFailedDetail(_logger, exception, subscription.Queue, eventId);
            if (IsTransient(exception))
            {
                LogTransientFailure(_logger, subscription.Queue, eventId, exceptionType);
                return new Outcome(OutcomeKind.RequeueTransient);
            }

            var failures = previousFailures + 1;
            LogHandlingFailed(_logger, subscription.Queue, eventId, failures, failure, stackTrace);
            return failures <= _options.RetryDelays.Count
                ? new Outcome(OutcomeKind.Republish, subscription.RetryQueue(_options.RetryDelays[failures - 1]), failures)
                : DeadLetter(subscription, properties.MessageId, failure, failures);
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "Only the table name is formatted in, from a schema validated as a snake_case identifier and quoted; values are parameters.")]
    private async Task<bool> RecordInInboxAsync(
        DatabaseSession session, IntegrationEventSubscription subscription, Guid eventId, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(subscription.InsertInboxSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = eventId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = subscription.Queue });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = _timeProvider.GetUtcNow() });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private Outcome DeadLetter(IntegrationEventSubscription subscription, string? messageId, string reason, int failures = 0)
    {
        LogDeadLettered(_logger, subscription.Queue, messageId, reason);
        return new Outcome(OutcomeKind.Republish, subscription.DeadLetterQueue, failures, reason);
    }

    private async Task RepublishAsync(BasicDeliverEventArgs delivery, string queue, int failures, string? failure)
    {
        var source = delivery.BasicProperties;
        var headers = source.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(source.Headers, StringComparer.Ordinal);
        headers[AttemptHeader] = failures;
        headers[FailedAtHeader] = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (failure is not null)
        {
            headers[FailureHeader] = failure;
        }

        var properties = new BasicProperties
        {
            MessageId = source.MessageId,
            Type = source.Type,
            ContentType = source.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = source.Timestamp,
            Headers = headers,
        };

        using var timeout = new CancellationTokenSource(s_confirmTimeout, _timeProvider);
        await _publishLock.WaitAsync(timeout.Token);
        try
        {
            if (_publishChannel is not { IsOpen: true })
            {
                if (_publishChannel is not null)
                {
                    await _publishChannel.DisposeAsync();
                }

                _publishChannel = await CreatePublishChannelAsync(await _rabbitMq.GetAsync(timeout.Token), timeout.Token);
            }

            // Mandatory: if the queue is missing, the publish fails instead of the message being dropped.
            await _publishChannel.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, delivery.Body, timeout.Token);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task PauseAsync(int failures)
    {
        var pause = TimeSpan.FromTicks(Math.Min(
            s_maxPause.Ticks,
            _options.RequeuePause.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 10))));
        try
        {
            await Task.Delay(pause, _timeProvider, _stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopping: requeue at once.
        }
    }

    private static int Track(ConcurrentDictionary<string, int> failures, string key)
    {
        if (failures.Count > MaxTrackedMessages)
        {
            // Entries normally go when their message is acknowledged; this bounds leftovers (for
            // example messages another worker finished).
            failures.Clear();
        }

        return failures.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    private void Forget(string key)
    {
        _requeueFailures.TryRemove(key, out _);
        _transientFailures.TryRemove(key, out _);
    }

    private async Task WaitForDeliveriesAsync(CancellationToken cancellationToken)
    {
        // Deliveries already prefetched are still dispatched after the cancel, so wait until none has
        // been running for two polls in a row.
        var idlePolls = 0;
        while (idlePolls < 2 && !cancellationToken.IsCancellationRequested)
        {
            idlePolls = Volatile.Read(ref _inFlight) == 0 ? idlePolls + 1 : 0;
            await Task.Delay(s_shutdownPoll, _timeProvider, CancellationToken.None);
        }
    }

    private static int ReadAttempt(IReadOnlyBasicProperties properties) =>
        properties.Headers?.TryGetValue(AttemptHeader, out var value) == true
            ? value switch
            {
                int number => number,
                long number => (int)Math.Min(number, int.MaxValue),
                byte number => number,
                short number => number,
                _ => 0,
            }
            : 0;

    private static string? HeaderString(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out var value) == true
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;

    private static Activity? StartProcessActivity(IntegrationEventSubscription subscription, IReadOnlyBasicProperties properties, Guid eventId)
    {
        ActivityContext.TryParse(
            HeaderString(properties, MessagingTelemetry.TraceParentHeader),
            HeaderString(properties, MessagingTelemetry.TraceStateHeader),
            out var parent);
        var activity = MessagingTelemetry.Source.StartActivity($"process {subscription.EventType}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", subscription.Queue);
        activity?.SetTag("messaging.message.id", eventId);
        return activity;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling event {EventId} from queue {Queue} failed with {Failure} (failure {Failures}). Stack trace without messages: {StackTrace}")]
    private static partial void LogHandlingFailed(ILogger logger, string queue, Guid eventId, int failures, string failure, string stackTrace);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handling event {EventId} from queue {Queue} failed.")]
    private static partial void LogHandlingFailedDetail(ILogger logger, Exception exception, string queue, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling event {EventId} from queue {Queue} hit a transient {ExceptionType}; it is requeued without using a retry.")]
    private static partial void LogTransientFailure(ILogger logger, string queue, Guid eventId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} from queue {Queue} moved to the dead-letter queue: {Reason}.")]
    private static partial void LogDeadLettered(ILogger logger, string queue, string? messageId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Acknowledging or republishing a delivery from queue {Queue} failed; it will be redelivered or rejected.")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string queue);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The consumer of queue {Queue} stopped unexpectedly; no events are handled from it until the worker restarts.")]
    private static partial void LogConsumerStopped(ILogger logger, string queue);

    private readonly record struct Outcome(OutcomeKind Kind, string? Queue = null, int Failures = 0, string? Failure = null);
}
