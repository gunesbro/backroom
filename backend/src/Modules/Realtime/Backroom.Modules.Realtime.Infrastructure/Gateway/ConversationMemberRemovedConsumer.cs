using Microsoft.Extensions.Logging;
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
/// <remarks>
/// The event can arrive late (a backlog, a redelivery). If the account may read the conversation again by then (it
/// rejoined, or it is an open public channel), the eviction is skipped: it would only cut a reader off from live events
/// it may have, and the after-commit path has already evicted the old subscription. The check runs in the consumer's
/// unit of work, under row-level security for the event's organization.
/// </remarks>
internal sealed partial class ConversationMemberRemovedConsumer(
    GroupRemovalBroadcast broadcast,
    IConversationReadAccess conversations,
    IOptions<RealtimeOptions> options,
    ILogger<ConversationMemberRemovedConsumer> logger)
    : IIntegrationEventHandler<ConversationMemberRemoved>
{
    public async Task HandleAsync(ConversationMemberRemoved integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (await conversations.CanReadAsync(integrationEvent.OrgId, integrationEvent.ConversationId, integrationEvent.AccountId, cancellationToken))
        {
            LogSkipped(logger, integrationEvent.AccountId, integrationEvent.ConversationId, integrationEvent.OrgId);
            return;
        }

        await broadcast.PublishConversationAsync(
            integrationEvent.OrgId, integrationEvent.ConversationId, integrationEvent.AccountId, options.Value.CommandTimeout, cancellationToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Account {AccountId} may read conversation {ConversationId} of organization {OrganizationId} again; the late removal was skipped.")]
    private static partial void LogSkipped(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId);
}
