using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Domain.Events;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// One consuming module's handler for one event type, with its queues:
/// <c>{consumer}.{event-type}</c> bound to the publisher's exchange, a retry queue per delay
/// (<c>….retry.{ms}ms</c>, which dead-letters back to the main queue), and <c>….dlq</c>.
/// </summary>
internal sealed class IntegrationEventSubscription
{
    private IntegrationEventSubscription(
        string eventType,
        string consumerSchema,
        Func<ReadOnlyMemory<byte>, ReceivedEvent?> deserialize,
        Func<IServiceProvider, object, CancellationToken, Task> handle)
    {
        EventType = eventType;
        PublisherModule = IntegrationEventTypes.ModuleOf(eventType);
        ConsumerSchema = consumerSchema;
        Queue = consumerSchema + "." + eventType;
        DeadLetterQueue = Queue + ".dlq";
        InsertInboxSql =
            $"INSERT INTO {OrganizationRowLevelSecurity.QualifiedName(consumerSchema, InboxTable.Name)} " +
            "(event_id, handler, org_id, processed_at) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING";
        Deserialize = deserialize;
        Handle = handle;
    }

    public string EventType { get; }

    public string PublisherModule { get; }

    public string ConsumerSchema { get; }

    /// <summary>The main queue; also the handler name recorded in the inbox.</summary>
    public string Queue { get; }

    public string DeadLetterQueue { get; }

    public string InsertInboxSql { get; }

    public Func<ReadOnlyMemory<byte>, ReceivedEvent?> Deserialize { get; }

    public Func<IServiceProvider, object, CancellationToken, Task> Handle { get; }

    public static IntegrationEventSubscription For<TEvent, THandler>(string consumerSchema)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        OrganizationRowLevelSecurity.ValidateIdentifier(consumerSchema, nameof(consumerSchema));
        return new IntegrationEventSubscription(
            IntegrationEventMetadata<TEvent>.EventType,
            consumerSchema,
            body => IntegrationEventSerializer.Deserialize<TEvent>(body.Span) is { } integrationEvent
                ? new ReceivedEvent(integrationEvent, integrationEvent.EventId, integrationEvent.OrgId)
                : null,
            (services, integrationEvent, cancellationToken) =>
                services.GetRequiredService<THandler>().HandleAsync((TEvent)integrationEvent, cancellationToken));
    }

    public string RetryQueue(TimeSpan delay) =>
        Queue + ".retry." + ((long)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";
}
