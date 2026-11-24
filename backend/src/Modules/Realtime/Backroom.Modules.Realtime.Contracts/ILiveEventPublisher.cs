namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// Publishes live events (docs/contracts/phase-1.md, "Live events") to the clients subscribed to a group.
/// Modules call it <b>after</b> their unit of work has committed, never inside the transaction, and a failure here
/// must never fail the request that caused the event: live events can be lost, and clients catch up with a read
/// after reconnecting. Implementations carry the payload to Valkey and the SignalR gateway; they never log it.
/// </summary>
public interface ILiveEventPublisher
{
    /// <summary>Sends <c>MessagePosted</c> to the channel's group.</summary>
    Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken);

    /// <summary>Sends <c>ChannelCreated</c> to the organization's group.</summary>
    Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken);

    /// <summary>Sends <c>PresenceChanged</c> to the person's presence group in the payload's organization.</summary>
    Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken);
}
