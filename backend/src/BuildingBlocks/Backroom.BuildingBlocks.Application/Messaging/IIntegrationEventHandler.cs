using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.BuildingBlocks.Application.Messaging;

/// <summary>
/// Handles an integration event in the consuming module. Each delivery runs in its own unit of work
/// in the event's organization; the event is recorded in the module's inbox in the same transaction,
/// so a redelivered event is skipped. Throwing rolls everything back and the delivery is retried with
/// back-off, then moved to the dead-letter queue. Handlers make no external calls of their own; side
/// effects go through the outbox. The one exception is the Email module's sending, which records each
/// delivery in its own transactions so that a redelivery never sends twice (Email's CLAUDE.md).
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
