namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// At most one delivered <c>Typing</c> per account and conversation per <see cref="RealtimeOptions.TypingInterval"/> on this
/// node (docs/contracts/phase-2.md, "Live events"). Per node on purpose: a typing event is never stored, costs one group send
/// and is worth nothing once stale, so a person whose connections sit on several nodes may get one event per node and
/// interval, not one more per call; a shared store would add a round trip to every call for no gain. Time is the monotonic
/// <see cref="TimeProvider.GetTimestamp"/>, so a wall-clock jump never holds typing back. The state is bounded: an entry
/// counts as expired one interval after it was taken, expired entries are swept at most once per interval when the table
/// is half full (and again when it is full), and <see cref="RealtimeOptions.MaxTypingEntries"/> is a hard limit: a table
/// still full after a sweep drops the call (typing is best effort). Entries are added only after the access check passed,
/// so callers of conversations they cannot post to never occupy space.
/// </summary>
internal sealed class TypingThrottle(TimeProvider timeProvider, RealtimeOptions options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid AccountId, Guid ConversationId), long> _taken = [];
    private long _lastSweep;
    private bool _swept;

    /// <summary>The cheap check before any access check: whether this account's last delivered event in the conversation is still within the interval.</summary>
    public bool IsThrottled(Guid accountId, Guid conversationId)
    {
        var now = timeProvider.GetTimestamp();
        lock (_lock)
        {
            return _taken.TryGetValue((accountId, conversationId), out var taken) && Within(taken, now);
        }
    }

    /// <summary>Takes the slot: <see langword="false"/> when another call took it meanwhile (or the table is full), and the call is dropped.</summary>
    public bool TryAcquire(Guid accountId, Guid conversationId)
    {
        var now = timeProvider.GetTimestamp();
        var key = (accountId, conversationId);
        lock (_lock)
        {
            if (_taken.TryGetValue(key, out var taken) && Within(taken, now))
            {
                return false;
            }

            var isNew = !_taken.ContainsKey(key);
            if (isNew && _taken.Count >= options.MaxTypingEntries / 2 && (!_swept || !Within(_lastSweep, now) || _taken.Count >= options.MaxTypingEntries))
            {
                Sweep(now);
                if (_taken.Count >= options.MaxTypingEntries)
                {
                    return false;
                }
            }

            _taken[key] = now;
            return true;
        }
    }

    private bool Within(long since, long now) => timeProvider.GetElapsedTime(since, now) < options.TypingInterval;

    private void Sweep(long now)
    {
        foreach (var expired in _taken.Where(entry => !Within(entry.Value, now)).Select(entry => entry.Key).ToList())
        {
            _taken.Remove(expired);
        }

        _lastSweep = now;
        _swept = true;
    }
}
