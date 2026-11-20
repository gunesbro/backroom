using RabbitMQ.Client;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Exchanges for integration events. Publishers and consumers declare them the same way, because
/// RabbitMQ rejects a redeclaration with different arguments.
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>Messages kept for events no queue is bound for, at most.</summary>
    public const int UnroutedMaxLength = 100_000;

    /// <summary>How long a message no queue was bound for is kept.</summary>
    public static readonly TimeSpan UnroutedRetention = TimeSpan.FromDays(7);

    /// <summary>Messages kept in a consumer's dead-letter queue, at most.</summary>
    public const int DeadLetterMaxLength = 100_000;

    /// <summary>How long a dead letter is kept: as long as the inbox remembers handled events.</summary>
    public static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(14);

    /// <summary>The alternate exchange and queue of <paramref name="exchange"/>.</summary>
    public static string UnroutedName(string exchange) => exchange + ".unrouted";

    /// <summary>
    /// Declares the module's durable topic exchange <c>{module}.events</c>. Its alternate exchange
    /// sends events that no queue is bound for (for example before a consumer has declared its queue
    /// on a fresh deployment) to the quorum queue <c>{module}.events.unrouted</c> instead of dropping
    /// them; operators can move them on from there. The queue is bounded by length and age.
    /// </summary>
    public static async Task DeclareEventExchangeAsync(IChannel channel, string module, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var exchange = IntegrationEventTypes.ExchangeFor(module);
        var unrouted = UnroutedName(exchange);

        await channel.ExchangeDeclareAsync(unrouted, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            unrouted,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
                ["x-max-length"] = UnroutedMaxLength,
                ["x-message-ttl"] = (long)UnroutedRetention.TotalMilliseconds,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(unrouted, unrouted, routingKey: string.Empty, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["alternate-exchange"] = unrouted },
            cancellationToken: cancellationToken);
    }
}
