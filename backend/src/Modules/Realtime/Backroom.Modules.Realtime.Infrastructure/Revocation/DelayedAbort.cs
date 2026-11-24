using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// Closes a connection <see cref="RealtimeOptions.RevocationCloseGrace"/> after its <c>SessionRevoked</c> notice was written, on a
/// <see cref="TimeProvider"/> timer, so the frame can leave before the transport is torn down and nobody waits for it. At most
/// <see cref="MaxPending"/> closes wait at once (bounded by the node's connections anyway); beyond that a connection is closed at once.
/// </summary>
internal sealed class DelayedAbort(IOptions<RealtimeOptions> options, TimeProvider timeProvider) : IDisposable
{
    internal const int MaxPending = 10_000;

    private readonly ConcurrentDictionary<ITimer, byte> _pending = new();

    /// <summary>Closes waiting for their grace; for tests.</summary>
    internal int Pending => _pending.Count;

    public void Schedule(Action abort)
    {
        ArgumentNullException.ThrowIfNull(abort);
        var grace = options.Value.RevocationCloseGrace;
        if (grace <= TimeSpan.Zero || _pending.Count >= MaxPending)
        {
            Run(abort);
            return;
        }

        ITimer? timer = null;
        timer = timeProvider.CreateTimer(
            _ =>
            {
                _ = _pending.TryRemove(timer!, out var _);
                timer!.Dispose();
                Run(abort);
            },
            null,
            grace,
            Timeout.InfiniteTimeSpan);
        _pending[timer] = 0;
    }

    // Connections end with the node; the timers must not outlive it.
    public void Dispose()
    {
        foreach (var timer in _pending.Keys)
        {
            timer.Dispose();
        }

        _pending.Clear();
    }

    private static void Run(Action abort)
    {
        abort();
        RealtimeTelemetry.RevokedConnections.Add(1);
    }
}
