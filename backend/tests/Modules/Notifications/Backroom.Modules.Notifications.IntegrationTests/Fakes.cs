using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Presence.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Notifications.IntegrationTests;

/// <summary>Messaging's read access: an account reads a conversation only while it is allowed here.</summary>
internal sealed class FakeConversationAccess : IConversationReadAccess
{
    private readonly HashSet<(Guid Conversation, Guid Account)> _allowed = [];

    public void Allow(Guid conversationId, params Guid[] accounts)
    {
        foreach (var account in accounts)
        {
            _allowed.Add((conversationId, account));
        }
    }

    /// <summary>The real check starts with active organization members.</summary>
    public FakeMembers? Members { get; set; }

    public void Revoke(Guid conversationId, Guid account) => _allowed.Remove((conversationId, account));

    public Task<bool> CanReadAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_allowed.Contains((conversationId, accountId)));

    public Task<bool> CanPostAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_allowed.Contains((conversationId, accountId)));

    public Task<IReadOnlyList<Guid>> FilterReadersAsync(Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. accountIds.Where(account => _allowed.Contains((conversationId, account)) && Members?.IsMember(account) != false)]);

    public Task<IReadOnlySet<Guid>> ReadableConversationsAsync(Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(conversationIds.Where(conversation => _allowed.Contains((conversation, accountId))).ToHashSet());

    public Task<bool> IsMemberAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_allowed.Contains((conversationId, accountId)));
}

/// <summary>The organization's member check: everyone is an active member except those marked removed (a guest is not a member either).</summary>
internal sealed class FakeMembers : Backroom.BuildingBlocks.Application.Organizations.IOrganizationMembershipCheck
{
    private readonly HashSet<Guid> _notMembers = [];

    public void Remove(Guid accountId) => _notMembers.Add(accountId);

    public bool IsMember(Guid accountId) => !_notMembers.Contains(accountId);

    public Task<bool> IsActiveMemberAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(!_notMembers.Contains(accountId));

    public Task<IReadOnlyList<Guid>> SelectActiveMembersAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. accountIds.Where(account => !_notMembers.Contains(account))]);
}

/// <summary>
/// Messaging's previews: a message belongs to a conversation, and the text goes only to accounts that may read it (the same
/// check as reading). Truncates like the real one.
/// </summary>
internal sealed class FakePreviews : IMessagePreviews
{
    private readonly Dictionary<Guid, (Guid Conversation, string Text)> _messages = [];

    public FakeConversationAccess? Access { get; set; }

    public List<int> RequestedLengths { get; } = [];

    public void Add(Guid messageId, Guid conversationId, string text) => _messages[messageId] = (conversationId, text);

    public async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(
        Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> messageIds, int maxLength, CancellationToken cancellationToken)
    {
        RequestedLengths.Add(maxLength);
        var result = new Dictionary<Guid, string>();
        foreach (var id in messageIds)
        {
            if (_messages.TryGetValue(id, out var message)
                && Access is not null
                && await Access.CanReadAsync(organizationId, message.Conversation, accountId, cancellationToken))
            {
                result[id] = message.Text.Length <= maxLength ? message.Text : message.Text[..maxLength];
            }
        }

        return result;
    }
}

/// <summary>Presence's reader: only the accounts set to do not disturb are answered with an end time.</summary>
internal sealed class FakePresence : IPresenceReader
{
    private readonly Dictionary<Guid, DateTimeOffset> _dnd = [];

    public void DoNotDisturb(Guid accountId, DateTimeOffset until) => _dnd[accountId] = until;

    public Task<IReadOnlyList<PresenceEntry>> ReadAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PresenceEntry>>(
            [.. accountIds.Select(id => new PresenceEntry(id, PresenceState.Online, _dnd.TryGetValue(id, out var until) ? until : null, null))]);
}

/// <summary>Realtime's publisher; records the notifications.</summary>
internal sealed class RecordingPublisher : ILiveEventPublisher
{
    public List<NotificationRequestedPayload> Notifications { get; } = [];

    public Task PublishAsync(NotificationRequestedPayload payload, CancellationToken cancellationToken)
    {
        Notifications.Add(payload);
        return Task.CompletedTask;
    }

    public Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(MessagePostedV2Payload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(MessageEditedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(MessageDeletedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(ConversationUpdatedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(MembershipChangedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(FileRejectedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveFromConversationAsync(Guid orgId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
}
