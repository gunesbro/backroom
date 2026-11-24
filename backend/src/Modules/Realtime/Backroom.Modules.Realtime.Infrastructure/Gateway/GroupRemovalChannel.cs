using StackExchange.Redis;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The Valkey pub/sub channel that tells every app node to take an account out of an organization's groups. The worker
/// publishes (it consumes <c>organization.member-removed.v1</c>); each app node listens and removes the account's own
/// connections, the only ones it can name. The message is the two ids, 32 bytes, nothing else.
/// </summary>
internal static class GroupRemovalChannel
{
    private const int MessageLength = 32;

    // The cell's prefix is added by the connection, so cells that share a server do not hear each other.
    public static RedisChannel Name { get; } = RedisChannel.Literal("realtime:member-removed");

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
}
