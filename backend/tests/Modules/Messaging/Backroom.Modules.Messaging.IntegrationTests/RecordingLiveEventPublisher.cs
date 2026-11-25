using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>Records what is published. <see cref="OnPublish"/> runs first, and <see cref="Fail"/> makes the publish throw.</summary>
internal sealed class RecordingLiveEventPublisher : ILiveEventPublisher
{
    private readonly Lock _lock = new();
    private readonly List<MessagePostedPayload> _messages = [];
    private readonly List<MessagePostedV2Payload> _messagesV2 = [];
    private readonly List<ChannelCreatedPayload> _channels = [];
    private readonly List<(Guid OrgId, Guid ConversationId, Guid AccountId)> _removals = [];
    private readonly List<ConversationUpdatedPayload> _conversations = [];
    private readonly List<MembershipChangedPayload> _memberships = [];

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

    public IReadOnlyList<MessagePostedV2Payload> MessagesV2
    {
        get
        {
            lock (_lock)
            {
                return [.. _messagesV2];
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

    public async Task PublishAsync(MessagePostedV2Payload payload, CancellationToken cancellationToken)
    {
        await BeforeAsync();
        lock (_lock)
        {
            _messagesV2.Add(payload);
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

    public IReadOnlyList<ConversationUpdatedPayload> Conversations
    {
        get
        {
            lock (_lock)
            {
                return [.. _conversations];
            }
        }
    }

    public IReadOnlyList<MembershipChangedPayload> Memberships
    {
        get
        {
            lock (_lock)
            {
                return [.. _memberships];
            }
        }
    }

    public async Task PublishAsync(ConversationUpdatedPayload payload, CancellationToken cancellationToken)
    {
        await BeforeAsync();
        lock (_lock)
        {
            _conversations.Add(payload);
        }
    }

    public async Task PublishAsync(MembershipChangedPayload payload, CancellationToken cancellationToken)
    {
        await BeforeAsync();
        lock (_lock)
        {
            _memberships.Add(payload);
        }
    }

    /// <summary>Messaging publishes no presence; recorded nowhere.</summary>
    public Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(NotificationRequestedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<(Guid OrgId, Guid ConversationId, Guid AccountId)> Removals
    {
        get
        {
            lock (_lock)
            {
                return [.. _removals];
            }
        }
    }

    public async Task RemoveFromConversationAsync(Guid orgId, Guid conversationId, Guid accountId, CancellationToken cancellationToken)
    {
        await BeforeAsync();
        lock (_lock)
        {
            _removals.Add((orgId, conversationId, accountId));
        }
    }

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
