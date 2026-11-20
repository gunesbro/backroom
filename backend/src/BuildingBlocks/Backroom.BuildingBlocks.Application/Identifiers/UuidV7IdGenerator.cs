using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Backroom.BuildingBlocks.Application.Identifiers;

/// <summary>
/// Monotonic UUIDv7 (RFC 9562, section 6.2, method 1): 48-bit Unix milliseconds from
/// <see cref="TimeProvider"/>, a 12-bit counter in <c>rand_a</c> that orders ids created in the same
/// millisecond, and 62 random bits. If the counter overflows or the clock goes backwards, the
/// timestamp is advanced by one millisecond, so ids from one generator always increase. Ids from
/// different processes are ordered only by their millisecond.
/// </summary>
public sealed class UuidV7IdGenerator : IIdGenerator
{
    private const int MaxCounter = 0xFFF;

    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private long _lastMilliseconds = -1;
    private int _counter;

    public UuidV7IdGenerator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public Guid NewId()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        long milliseconds;
        int counter;
        lock (_lock)
        {
            milliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (milliseconds > _lastMilliseconds)
            {
                // New millisecond: start the counter at a random value with the top bit clear,
                // leaving room to increment (RFC 9562, section 6.2).
                _lastMilliseconds = milliseconds;
                _counter = ((random[0] & 0x07) << 8) | random[1];
            }
            else if (_counter < MaxCounter)
            {
                _counter++;
            }
            else
            {
                _lastMilliseconds++;
                _counter = 0;
            }

            milliseconds = _lastMilliseconds;
            counter = _counter;
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, milliseconds << 16);
        bytes[6] = (byte)(0x70 | (counter >> 8));
        bytes[7] = (byte)counter;
        random[2..].CopyTo(bytes[8..]);
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));

        return new Guid(bytes, bigEndian: true);
    }
}
