namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// Publishes live events (docs/contracts/phase-2.md, "Live events") to the clients subscribed to a group.
/// Modules call it <b>after</b> their unit of work has committed, never inside the transaction, and a failure here
/// must never fail the request that caused the event: live events can be lost, and clients catch up with a read
/// after reconnecting. Implementations carry the payload to Valkey and the SignalR gateway; they never log it.
/// </summary>
public interface ILiveEventPublisher
{
    /// <summary>Sends <c>MessagePosted</c> to the conversation's group.</summary>
    Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken);

    /// <summary>Sends <c>ChannelCreated</c> to the organization's group.</summary>
    Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken);

    /// <summary>Sends <c>PresenceChanged</c> to the person's presence group in the payload's organization.</summary>
    Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the account's connections out of the conversation's group on every node, after the commit that ended its
    /// membership. Best effort and bounded like a publish (it never throws): the durable path is Realtime's consumer of
    /// <c>messaging.conversation-member-removed.v1</c>, which repeats it; removing twice is harmless.
    /// </summary>
    Task RemoveFromConversationAsync(Guid orgId, Guid conversationId, Guid accountId, CancellationToken cancellationToken);
}
