using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>Records what is published. <see cref="OnPublish"/> runs first, and <see cref="Fail"/> makes the publish throw.</summary>
internal sealed class RecordingLiveEventPublisher : ILiveEventPublisher
{
    private readonly Lock _lock = new();
    private readonly List<MessagePostedPayload> _messages = [];
    private readonly List<ChannelCreatedPayload> _channels = [];

    public bool Fail { get; set; }

    /// <summary>Called at publish time, for checks about what was committed by then.</summary>
    public Func<Task>? OnPublish { get; set; }

    public IReadOnlyList<MessagePostedPayload> Messages
    {
        get
        {
            lock (_lock)
            {
                return [.. _messages];
            }
        }
    }

    public IReadOnlyList<ChannelCreatedPayload> Channels
    {
        get
        {
            lock (_lock)
            {
                return [.. _channels];
            }
        }
    }

    public async Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken)
    {
        await BeforeAsync();
        lock (_lock)
        {
            _messages.Add(payload);
        }
    }

    public async Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken)
    {
        await BeforeAsync();
        lock (_lock)
        {
            _channels.Add(payload);
        }
    }

    /// <summary>Messaging publishes no presence; recorded nowhere.</summary>
    public Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task BeforeAsync()
    {
        if (OnPublish is { } hook)
        {
            await hook();
        }

        if (Fail)
        {
            throw new InvalidOperationException("The publisher is down.");
        }
    }
}
