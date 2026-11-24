using System.Buffers.Binary;
using System.Text;
using StackExchange.Redis;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// The Valkey pub/sub channel that tells every app node about a revocation: each node takes it into its cache and closes its own
/// matching connections (a node only knows its own). The message is the account id (16 bytes), <c>revoked_at</c> and
/// <c>keep_until</c> (Unix milliseconds, 8 bytes each) and the session id in UTF-8 (empty for every session of the account):
/// at most 168 bytes, no names or emails. The cell's prefix is added by the connection.
/// </summary>
internal static class SessionRevocationChannel
{
    private const int FixedLength = 32;

    public static RedisChannel Name { get; } = RedisChannel.Literal("realtime:session-revoked");

    public static byte[] Encode(SessionRevocation revocation)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        var sessionId = revocation.SessionId is null ? [] : Encoding.UTF8.GetBytes(revocation.SessionId);
        var message = new byte[FixedLength + sessionId.Length];
        revocation.AccountId.TryWriteBytes(message.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(16, 8), revocation.RevokedAt.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(24, 8), revocation.KeepUntil.ToUnixTimeMilliseconds());
        sessionId.CopyTo(message.AsSpan(FixedLength));
        return message;
    }

    public static bool TryDecode(ReadOnlySpan<byte> message, out SessionRevocation? revocation)
    {
        revocation = null;
        if (message.Length is < FixedLength or > FixedLength + ValkeyRevocationStore.MaxSessionIdLength * 4)
        {
            return false;
        }

        var accountId = new Guid(message[..16]);
        var revokedAt = BinaryPrimitives.ReadInt64BigEndian(message.Slice(16, 8));
        var keepUntil = BinaryPrimitives.ReadInt64BigEndian(message.Slice(24, 8));
        if (accountId == Guid.Empty || revokedAt is < 0 or > 253_402_300_799_000 || keepUntil is < 0 or > 253_402_300_799_000)
        {
            return false;
        }

        string? sessionId = null;
        if (message.Length > FixedLength)
        {
            try
            {
                sessionId = new UTF8Encoding(false, true).GetString(message[FixedLength..]);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (sessionId.Length > ValkeyRevocationStore.MaxSessionIdLength)
            {
                return false;
            }
        }

        revocation = new SessionRevocation(accountId, sessionId, DateTimeOffset.FromUnixTimeMilliseconds(revokedAt), DateTimeOffset.FromUnixTimeMilliseconds(keepUntil));
        return true;
    }
}
