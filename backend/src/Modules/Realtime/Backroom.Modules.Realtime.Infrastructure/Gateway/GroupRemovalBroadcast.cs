using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Tells every app node (through <see cref="GroupRemovalChannel"/>) to take the account out of groups. It throws when
/// Valkey does not accept the message in time, so an event consumer fails and RabbitMQ delivers the event again: a
/// removal is never silently lost at this step. Removing twice is harmless.
/// </summary>
internal sealed class GroupRemovalBroadcast(ValkeyConnection valkey, IOptions<RealtimeOptions> options, TimeProvider timeProvider)
{
    /// <summary>Every group of the organization (the member left or was removed from the organization).</summary>
    public Task PublishAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken) =>
        SendAsync(GroupRemovalChannel.Name, GroupRemovalChannel.Encode(organizationId, accountId), options.Value.CommandTimeout, cancellationToken);

    /// <summary>The conversation's group (the member left or was removed from the conversation), waiting at most <paramref name="timeout"/>.</summary>
    public Task PublishConversationAsync(Guid organizationId, Guid conversationId, Guid accountId, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(
            GroupRemovalChannel.ConversationName,
            GroupRemovalChannel.EncodeConversation(organizationId, conversationId, accountId),
            timeout,
            cancellationToken);

    private async Task SendAsync(RedisChannel channel, byte[] message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connection = await valkey.GetAsync(cancellationToken);
        var send = connection.GetSubscriber().PublishAsync(channel, message);
        await send.WaitAsync(timeout, timeProvider, cancellationToken);
    }
}
