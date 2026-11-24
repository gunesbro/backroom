using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure;

/// <summary>
/// Placeholder until the Valkey and SignalR implementation arrives (Phase 1 step 6.2), which replaces it: events are
/// dropped, and clients read the messages they missed.
/// </summary>
internal sealed class NoOpLiveEventPublisher : ILiveEventPublisher
{
    public Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;
}
