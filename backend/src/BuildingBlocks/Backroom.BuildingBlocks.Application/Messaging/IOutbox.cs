using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.BuildingBlocks.Application.Messaging;

/// <summary>
/// Adds integration events to the publishing module's outbox in the current unit of work, so an
/// event is published if and only if the state change that caused it is committed. The outbox
/// relay publishes it to RabbitMQ afterwards, at least once and in no guaranteed order: events that
/// must be applied in order carry a version.
/// </summary>
public interface IOutbox
{
    Task AddAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent;
}
