using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Worker role: <c>messaging.conversation-member-removed.v1</c> → every app node removes the account's connections from
/// that conversation's group (<see cref="GroupRemovalBroadcast"/>). This is the durable second path: Messaging already
/// asked for the removal right after its commit (<see cref="Contracts.ILiveEventPublisher.RemoveFromConversationAsync"/>),
/// which is best effort. Removing is idempotent, so the repeat and a redelivered event are harmless; when Valkey is
/// down the handler throws and the event is delivered again.
/// </summary>
internal sealed class ConversationMemberRemovedConsumer(GroupRemovalBroadcast broadcast, IOptions<RealtimeOptions> options)
    : IIntegrationEventHandler<ConversationMemberRemoved>
{
    public Task HandleAsync(ConversationMemberRemoved integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return broadcast.PublishConversationAsync(
            integrationEvent.OrgId, integrationEvent.ConversationId, integrationEvent.AccountId, options.Value.CommandTimeout, cancellationToken);
    }
}
