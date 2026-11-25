using System.Buffers.Binary;
using System.Buffers.Text;

namespace Backroom.Modules.Notifications.Application.Bell;

/// <summary>
/// The opaque cursor of the bell: base64url of a version byte, the creation time (UTC ticks) and the id of the last entry of
/// the page, so the next page continues after exactly that row of <c>(created_at DESC, id DESC)</c>.
/// </summary>
internal static class BellCursor
{
    private const byte Version = 1;
    private const int PayloadLength = 1 + sizeof(long) + 16;

    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..], createdAt.UtcTicks);
        id.TryWriteBytes(payload[(1 + sizeof(long))..], bigEndian: true, out _);
        return Base64Url.EncodeToString(payload);
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (cursor is not { Length: > 0 and <= 64 })
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(cursor, payload, out written))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        if (written != PayloadLength || payload[0] != Version)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload[1..]);
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        id = new Guid(payload[(1 + sizeof(long))..], bigEndian: true);
        return true;
    }
}
