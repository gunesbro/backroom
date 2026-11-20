using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Backroom.BuildingBlocks.Application.Identifiers;

namespace Backroom.BuildingBlocks.UnitTests.Identifiers;

public sealed class UuidV7IdGeneratorTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 25, 12, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void NewId_WhenCreated_HasVersion7AndRfcVariant()
    {
        var generator = new UuidV7IdGenerator(TimeProvider.System);

        var id = generator.NewId();

        id.Version.Should().Be(7);
        (id.ToByteArray(bigEndian: true)[8] & 0xC0).Should().Be(0x80);
    }

    [Fact]
    public void NewId_WhenCreated_EmbedsTheTimestamp()
    {
        var generator = new UuidV7IdGenerator(new FakeTimeProvider(s_start));

        TimestampOf(generator.NewId()).Should().Be(s_start.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void NewId_WhenTimeAdvances_SortsByCreationTime()
    {
        var time = new FakeTimeProvider(s_start);
        var generator = new UuidV7IdGenerator(time);

        var first = generator.NewId();
        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = generator.NewId();

        Compare(first, second).Should().BeNegative();
    }

    [Fact]
    public void NewId_WhenManyInTheSameMillisecond_StrictlyIncreases()
    {
        var generator = new UuidV7IdGenerator(new FakeTimeProvider(s_start));

        var ids = Enumerable.Range(0, 5_000).Select(_ => generator.NewId()).ToList();

        ids.Zip(ids.Skip(1)).Should().OnlyContain(pair => Compare(pair.First, pair.Second) < 0);
    }

    [Fact]
    public void NewId_WhenCounterOverflows_AdvancesTheTimestamp()
    {
        var generator = new UuidV7IdGenerator(new FakeTimeProvider(s_start));

        // The counter starts at or below 0x7FF and ends at 0xFFF, so 0x1001 ids overflow it at least once.
        // A loop, not Range().Select().Last(): LINQ would run the selector for the last index only.
        var last = Guid.Empty;
        for (var i = 0; i < 0x1001; i++)
        {
            last = generator.NewId();
        }

        TimestampOf(last).Should().BeGreaterThan(s_start.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void NewId_WhenClockGoesBackwards_StillIncreases()
    {
        var time = new ManualTimeProvider { UtcNow = s_start };
        var generator = new UuidV7IdGenerator(time);

        var first = generator.NewId();
        time.UtcNow = s_start.AddSeconds(-5);
        var second = generator.NewId();

        Compare(first, second).Should().BeNegative();
        TimestampOf(second).Should().Be(s_start.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void NewId_WhenCalledManyTimes_IsUnique()
    {
        var generator = new UuidV7IdGenerator(new FakeTimeProvider(s_start));

        var ids = Enumerable.Range(0, 10_000).Select(_ => generator.NewId()).ToHashSet();

        ids.Should().HaveCount(10_000);
    }

    [Fact]
    public void Constructor_WhenTimeProviderIsNull_Throws()
    {
        var create = () => new UuidV7IdGenerator(null!);

        create.Should().Throw<ArgumentNullException>();
    }

    private static long TimestampOf(Guid id)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        return ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
            | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
    }

    // RFC 9562 byte order, which is how PostgreSQL stores and sorts uuid values.
    private static int Compare(Guid left, Guid right) =>
        left.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(right.ToByteArray(bigEndian: true));

    // FakeTimeProvider cannot move backwards; system clocks can (NTP corrections).
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
