using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Publishes committed outbox events to RabbitMQ (docs/adr/0006). Each round locks a batch of
/// unpublished rows with <c>FOR UPDATE SKIP LOCKED</c> (so several relays can run side by side),
/// publishes them with publisher confirms, and marks them published in the same transaction. If
/// publishing or the commit fails, the rows stay unpublished and are sent again: delivery is at least
/// once. Events go to the module's topic exchange <c>{module}.events</c> with the event type as routing
/// key; events no queue is bound for are kept by the exchange's alternate exchange
/// (<see cref="RabbitMqTopology"/>).
/// </summary>
/// <remarks>
/// A row that can never be published (invalid type, another module's event, oversized payload) is
/// marked failed and skipped, so it cannot block the rows behind it. A schema whose round fails is
/// retried with back-off without delaying the other schemas.
/// </remarks>
public sealed partial class OutboxRelay : BackgroundService
{
    /// <summary>Longest wait for the broker's confirms of one batch, for example during a resource alarm.</summary>
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(30);

    private readonly OutboxRelayOptions _options;
    private readonly RabbitMqConnection _rabbitMq;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxRelay> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly Dictionary<string, Statements> _statements;
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _schemaFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _declaredModules = new(StringComparer.Ordinal);
    private IChannel? _channel;
    private bool _roleVerified;

    public OutboxRelay(IOptions<OutboxRelayOptions> options, RabbitMqConnection rabbitMq, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options.Value;
        _rabbitMq = rabbitMq;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<OutboxRelay>();
        _statements = _options.Schemas.ToDictionary(schema => schema, Statements.For, StringComparer.Ordinal);

        var connectionString = new NpgsqlConnectionStringBuilder(_options.ConnectionString)
        {
            MaxPoolSize = _options.MaxPoolSize,
            IncludeErrorDetail = false,
            NoResetOnClose = false,
        };
        _dataSource = new NpgsqlDataSourceBuilder(connectionString.ConnectionString)
            .UseLoggerFactory(loggerFactory)
            // Polling must not create a trace per round: commands are traced only inside a relay span,
            // which starts once a batch has rows.
            .ConfigureTracing(tracing => tracing
                .EnablePhysicalOpenTracing(false)
                .ConfigureCommandFilter(_ => Activity.Current is not null))
            .Build();
    }

    public override void Dispose()
    {
        _dataSource.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Publishes one batch from each outbox that is not backing off after a failure; returns whether
    /// any outbox had a full batch (more may be waiting).
    /// </summary>
    [SuppressMessage("Design", "CA1031", Justification = "One schema's failure must not stop the others; it is logged and retried with back-off.")]
    internal async Task<bool> RelayOnceAsync(CancellationToken cancellationToken)
    {
        var moreWaiting = false;
        foreach (var schema in _options.Schemas)
        {
            var now = _timeProvider.GetUtcNow();
            if (_schemaFailures.TryGetValue(schema, out var failure) && now < failure.RetryAt)
            {
                continue;
            }

            try
            {
                moreWaiting |= await RelayBatchAsync(schema, cancellationToken) == _options.BatchSize;
                _schemaFailures.Remove(schema);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var failures = failure.Failures + 1;
                _schemaFailures[schema] = (failures, now + Backoff(failures));
                LogRelayFailed(_logger, exception, schema, failures);
            }
        }

        return moreWaiting;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Only the table name is formatted in, from a schema validated as a snake_case identifier and quoted; values are parameters.")]
    internal async Task<int> RelayBatchAsync(string schema, CancellationToken cancellationToken)
    {
        var statements = _statements[schema];
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureRelayRoleAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var rows = await ReadBatchAsync(connection, transaction, statements.SelectBatch, _options.BatchSize, cancellationToken);
        if (rows.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        using var activity = MessagingTelemetry.Source.StartActivity($"outbox relay {schema}");
        activity?.SetTag("messaging.batch.message_count", rows.Count);

        var publishable = new List<OutboxRow>(rows.Count);
        var failed = new List<Guid>();
        foreach (var row in rows)
        {
            if (CannotPublish(row, schema) is { } reason)
            {
                failed.Add(row.Id);
                LogParked(_logger, row.Id, schema, reason);
            }
            else
            {
                publishable.Add(row);
            }
        }

        if (publishable.Count > 0)
        {
            using var timeout = new CancellationTokenSource(PublishTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await PublishAsync(schema, publishable, linked.Token);
            await MarkAsync(connection, transaction, statements.MarkPublished, publishable.Select(row => row.Id).ToArray(), cancellationToken);
        }

        if (failed.Count > 0)
        {
            await MarkAsync(connection, transaction, statements.MarkFailed, failed.ToArray(), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        LogPublished(_logger, publishable.Count, schema);
        return rows.Count;
    }

    /// <summary>
    /// Deletes events published longer ago than the published retention, and inbox rows older than the
    /// inbox retention, in bounded batches.
    /// </summary>
    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var deleted = 0;
        var now = _timeProvider.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var schema in _options.Schemas)
        {
            deleted += await DeleteInBatchesAsync(connection, _statements[schema].DeletePublished, now - _options.PublishedRetention, cancellationToken);
        }

        foreach (var schema in _options.InboxSchemas)
        {
            deleted += await DeleteInBatchesAsync(connection, InboxCleanupSql(schema), now - _options.InboxRetention, cancellationToken);
        }

        return deleted;
    }

    private static string InboxCleanupSql(string schema)
    {
        var table = OrganizationRowLevelSecurity.QualifiedName(schema, InboxTable.Name);
        return $"DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM {table} WHERE processed_at < $1 LIMIT $2)";
    }

    [SuppressMessage("Security", "CA2100", Justification = "Only the table name is formatted in, from a schema validated as a snake_case identifier and quoted; values are parameters.")]
    private static async Task<int> DeleteInBatchesAsync(NpgsqlConnection connection, string sql, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;
        int batch;
        do
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = cutoff });
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = OutboxRelayOptions.MaxBatchSize });
            batch = await command.ExecuteNonQueryAsync(cancellationToken);
            deleted += batch;
        }
        while (batch == OutboxRelayOptions.MaxBatchSize);

        return deleted;
    }

    [SuppressMessage("Design", "CA1031", Justification = "The relay must survive outages; cleanup failures are logged and retried at the next interval.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = _timeProvider.GetUtcNow();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var moreWaiting = await RelayOnceAsync(stoppingToken);

                if (_timeProvider.GetUtcNow() >= nextCleanup)
                {
                    nextCleanup = _timeProvider.GetUtcNow() + _options.CleanupInterval;
                    try
                    {
                        await CleanupAsync(stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        LogCleanupFailed(_logger, exception);
                    }
                }

                if (!moreWaiting)
                {
                    await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            // Here rather than in StopAsync, so the channel is never disposed while a round still uses it.
            await ResetChannelAsync();
        }
    }

    private static string? CannotPublish(OutboxRow row, string schema)
    {
        if (!IntegrationEventTypes.IsValid(row.EventType))
        {
            return "invalid event type";
        }

        if (!string.Equals(IntegrationEventTypes.ModuleOf(row.EventType), schema, StringComparison.Ordinal))
        {
            return "event type of another module";
        }

        return row.Payload.Length > OutboxTable.MaxPayloadBytes ? "payload too large" : null;
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(s_maxBackoff.TotalMilliseconds, 500 * Math.Pow(2, Math.Min(failures - 1, 10))));

    [SuppressMessage("Security", "CA2100", Justification = "Only the table name is formatted in, from a schema validated as a snake_case identifier and quoted; values are parameters.")]
    private static async Task<List<OutboxRow>> ReadBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, int batchSize, CancellationToken cancellationToken)
    {
        var rows = new List<OutboxRow>(batchSize);
        await using var select = new NpgsqlCommand(sql, connection, transaction);
        select.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Only the table name is formatted in, from a schema validated as a snake_case identifier and quoted; values are parameters.")]
    private async Task MarkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid[] ids, CancellationToken cancellationToken)
    {
        await using var mark = new NpgsqlCommand(sql, connection, transaction);
        mark.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = _timeProvider.GetUtcNow() });
        mark.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = ids });
        await mark.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureRelayRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (_roleVerified)
        {
            return;
        }

        // BYPASSRLS to read every organization's outbox, but neither a superuser nor an owner of tables.
        if (!await DatabaseRoleGuard.IsDedicatedBypassRoleAsync(connection, cancellationToken))
        {
            throw new InvalidOperationException(
                "The outbox relay role must have BYPASSRLS, must be neither a superuser nor an owner of database objects, must hold no server privileges and must have no role memberships.");
        }

        _roleVerified = true;
    }

    [SuppressMessage("Design", "CA1031", Justification = "Remaining confirms are observed after the first failure, which is rethrown.")]
    private async Task PublishAsync(string schema, List<OutboxRow> rows, CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(cancellationToken);
        var exchange = IntegrationEventTypes.ExchangeFor(schema);
        var confirms = new List<ValueTask>(rows.Count);
        try
        {
            if (_declaredModules.Add(schema))
            {
                await RabbitMqTopology.DeclareEventExchangeAsync(channel, schema, cancellationToken);
            }

            foreach (var row in rows)
            {
                using var activity = StartPublishActivity(row, exchange);
                var traceParent = activity?.Id ?? row.TraceParent;
                var traceState = activity?.TraceStateString ?? row.TraceState;

                var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [MessagingTelemetry.OrganizationHeader] = row.OrgId.ToString("D", CultureInfo.InvariantCulture),
                };
                if (traceParent is not null)
                {
                    headers[MessagingTelemetry.TraceParentHeader] = traceParent;
                }

                if (traceState is not null)
                {
                    headers[MessagingTelemetry.TraceStateHeader] = traceState;
                }

                var properties = new BasicProperties
                {
                    MessageId = row.Id.ToString("D", CultureInfo.InvariantCulture),
                    Type = row.EventType,
                    ContentType = IntegrationEventSerializer.ContentType,
                    DeliveryMode = DeliveryModes.Persistent,
                    Timestamp = new AmqpTimestamp(row.OccurredAt.ToUnixTimeSeconds()),
                    Headers = headers,
                };

                // With confirmation tracking, the task completes when the broker confirms the message.
                confirms.Add(channel.BasicPublishAsync(exchange, row.EventType, mandatory: false, properties, row.Payload, cancellationToken));
            }

            foreach (var confirm in confirms)
            {
                await confirm;
            }
        }
        catch
        {
            // Observe the remaining confirms, then drop the channel: the next batch opens a new one.
            foreach (var confirm in confirms)
            {
                try
                {
                    await confirm;
                }
                catch (Exception)
                {
                    // Already failing; the first exception is rethrown.
                }
            }

            await ResetChannelAsync();
            throw;
        }
    }

    private static Activity? StartPublishActivity(OutboxRow row, string exchange)
    {
        ActivityContext.TryParse(row.TraceParent, row.TraceState, out var parent);
        var activity = MessagingTelemetry.Source.StartActivity($"publish {row.EventType}", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.message.id", row.Id);
        return activity;
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await ResetChannelAsync();
        var connection = await _rabbitMq.GetAsync(cancellationToken);
        _channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        return _channel;
    }

    private async Task ResetChannelAsync()
    {
        _declaredModules.Clear();
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {Count} outbox events from schema {Schema}.")]
    private static partial void LogPublished(ILogger logger, int count, string schema);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox event {EventId} in schema {Schema} cannot be published ({Reason}); it is marked failed and skipped.")]
    private static partial void LogParked(ILogger logger, Guid eventId, string schema, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay round for schema {Schema} failed ({Failures} in a row); retrying with back-off.")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception, string schema, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox cleanup failed; retrying at the next interval.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    private sealed record OutboxRow(Guid Id, Guid OrgId, string EventType, byte[] Payload, string? TraceParent, string? TraceState, DateTimeOffset OccurredAt);

    private sealed record Statements(string SelectBatch, string MarkPublished, string MarkFailed, string DeletePublished)
    {
        public static Statements For(string schema)
        {
            var table = OrganizationRowLevelSecurity.QualifiedName(schema, OutboxTable.Name);
            return new Statements(
                $"""
                SELECT id, org_id, event_type, payload, trace_parent, trace_state, occurred_at
                FROM {table} WHERE published_at IS NULL AND failed_at IS NULL ORDER BY id LIMIT $1 FOR UPDATE SKIP LOCKED
                """,
                $"UPDATE {table} SET published_at = $1 WHERE id = ANY($2)",
                $"UPDATE {table} SET failed_at = $1 WHERE id = ANY($2)",
                $"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} WHERE published_at < $1 ORDER BY published_at LIMIT $2)");
        }
    }
}
