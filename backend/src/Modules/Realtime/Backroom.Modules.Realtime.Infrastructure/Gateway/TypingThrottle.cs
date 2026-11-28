namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// At most one delivered <c>Typing</c> per account and conversation per <see cref="RealtimeOptions.TypingInterval"/> on this
/// node (docs/contracts/phase-2.md, "Live events"). Per node on purpose: a typing event is never stored, costs one group send
/// and is worth nothing once stale, so a person whose connections sit on several nodes may get one event per node and
/// interval, not one more per call; a shared store would add a round trip to every call for no gain. The state is bounded
/// (<see cref="RealtimeOptions.MaxTypingEntries"/> entries, each living one interval); a full table is swept of expired
/// entries and, if still full, drops the call (typing is best effort). Entries are added only after the access check passed,
/// so callers of conversations they cannot post to never occupy space.
/// </summary>
internal sealed class TypingThrottle(TimeProvider timeProvider, RealtimeOptions options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid AccountId, Guid ConversationId), long> _expiry = [];

    /// <summary>The cheap check before any access check: whether this account's last delivered event in the conversation is still within the interval.</summary>
    public bool IsThrottled(Guid accountId, Guid conversationId)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        lock (_lock)
        {
            return _expiry.TryGetValue((accountId, conversationId), out var until) && now < until;
        }
    }

    /// <summary>Takes the slot: <see langword="false"/> when another call took it meanwhile (or the table is full), and the call is dropped.</summary>
    public bool TryAcquire(Guid accountId, Guid conversationId)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var key = (accountId, conversationId);
        lock (_lock)
        {
            if (_expiry.TryGetValue(key, out var until) && now < until)
            {
                return false;
            }

            if (!_expiry.ContainsKey(key) && _expiry.Count >= options.MaxTypingEntries)
            {
                foreach (var expired in _expiry.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
                {
                    _expiry.Remove(expired);
                }

                if (_expiry.Count >= options.MaxTypingEntries)
                {
                    return false;
                }
            }

            _expiry[key] = now + options.TypingInterval.Ticks;
            return true;
        }
    }
}
