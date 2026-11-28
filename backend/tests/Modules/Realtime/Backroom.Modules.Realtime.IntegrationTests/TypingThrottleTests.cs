using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure.Gateway;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>The typing throttle alone (no containers): interval, monotonic time and the bounded table.</summary>
public sealed class TypingThrottleTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public void TryAcquire_WithinTheInterval_DropsAndAfterItAllows()
    {
        var throttle = new TypingThrottle(_time, new RealtimeOptions());
        var account = Guid.NewGuid();
        var conversation = Guid.NewGuid();

        throttle.TryAcquire(account, conversation).Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(2.9));
        throttle.IsThrottled(account, conversation).Should().BeTrue();
        throttle.TryAcquire(account, conversation).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(0.1));
        throttle.TryAcquire(account, conversation).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_WhenFull_SweepsExpiredEntriesAndThenDropsCalls()
    {
        var options = new RealtimeOptions { MaxTypingEntries = 16 };
        var throttle = new TypingThrottle(_time, options);
        var account = Guid.NewGuid();
        for (var i = 0; i < 16; i++)
        {
            throttle.TryAcquire(account, Guid.NewGuid()).Should().BeTrue();
        }

        throttle.TryAcquire(account, Guid.NewGuid()).Should().BeFalse("a full table drops the call");
        _time.Advance(options.TypingInterval);
        throttle.TryAcquire(account, Guid.NewGuid()).Should().BeTrue("expired entries are swept");
    }

    [Fact]
    public void TryAcquire_WhenHalfFull_SweepsExpiredEntriesBeforeTheTableFills()
    {
        var options = new RealtimeOptions { MaxTypingEntries = 16 };
        var throttle = new TypingThrottle(_time, options);
        var account = Guid.NewGuid();
        // Steady load: 8 new entries per interval never reach the hard limit because each interval's expired ones are swept.
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < 8; i++)
            {
                throttle.TryAcquire(account, Guid.NewGuid()).Should().BeTrue($"round {round}");
            }

            _time.Advance(options.TypingInterval);
        }
    }
}
