using StackExchange.Redis;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The Valkey pub/sub channels that tell every app node to take an account's connections out of groups. A node only
/// knows its own connections, so removal is a broadcast; each app node listens (<see cref="GroupRemovalListener"/>) and
/// removes the connections it can name. Messages carry ids only: on <see cref="Name"/> the organization and the account
/// (32 bytes; every group of that organization), on <see cref="ConversationName"/> the organization, the conversation
/// and the account (48 bytes; that conversation's group).
/// </summary>
internal static class GroupRemovalChannel
{
    private const int MessageLength = 32;
    private const int ConversationMessageLength = 48;

    // The cell's prefix is added by the connection, so cells that share a server do not hear each other.
    public static RedisChannel Name { get; } = RedisChannel.Literal("realtime:member-removed");

    public static RedisChannel ConversationName { get; } = RedisChannel.Literal("realtime:conversation-member-removed");

    public static byte[] Encode(Guid organizationId, Guid accountId)
    {
        var message = new byte[MessageLength];
        organizationId.TryWriteBytes(message.AsSpan(0, 16));
        accountId.TryWriteBytes(message.AsSpan(16, 16));
        return message;
    }

    public static bool TryDecode(ReadOnlySpan<byte> message, out Guid organizationId, out Guid accountId)
    {
        organizationId = default;
        accountId = default;
        if (message.Length != MessageLength)
        {
            return false;
        }

        organizationId = new Guid(message[..16]);
        accountId = new Guid(message[16..]);
        return organizationId != Guid.Empty && accountId != Guid.Empty;
    }

    public static byte[] EncodeConversation(Guid organizationId, Guid conversationId, Guid accountId)
    {
        var message = new byte[ConversationMessageLength];
        organizationId.TryWriteBytes(message.AsSpan(0, 16));
        conversationId.TryWriteBytes(message.AsSpan(16, 16));
        accountId.TryWriteBytes(message.AsSpan(32, 16));
        return message;
    }

    public static bool TryDecodeConversation(ReadOnlySpan<byte> message, out Guid organizationId, out Guid conversationId, out Guid accountId)
    {
        organizationId = default;
        conversationId = default;
        accountId = default;
        if (message.Length != ConversationMessageLength)
        {
            return false;
        }

        organizationId = new Guid(message[..16]);
        conversationId = new Guid(message[16..32]);
        accountId = new Guid(message[32..]);
        return organizationId != Guid.Empty && conversationId != Guid.Empty && accountId != Guid.Empty;
    }
}
