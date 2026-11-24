using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// Decides whether a token's session or account is revoked (docs/contracts/phase-1.md, "Identity and tokens"), for every
/// request and hub connect, without a Valkey round trip per request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache.</b> In-memory maps in front of Valkey, bounded (<see cref="RealtimeOptions.RevocationCacheMaxEntries"/>, 100,000 by default;
/// over the limit the oldest "not revoked" answers are evicted, never a revocation in force and never everything) and expiring: a "revoked" entry lasts until its <c>keep_until</c> (a revocation never un-revokes), a
/// "not revoked" entry for <see cref="RealtimeOptions.RevocationCacheTtl"/>. Each active session costs one Valkey read (one
/// <c>MGET</c> for the session and the account) per TTL, not one per request.
/// </para>
/// <para>
/// <b>Latency.</b> A revocation reaches every app node's cache at once through the <c>SessionRevoked</c> broadcast
/// (<see cref="Learn"/>), so it normally takes effect in milliseconds after the cell applied it. If a node missed the
/// broadcast, its cached "not revoked" expires within <see cref="RealtimeOptions.RevocationCacheTtl"/> (5 s) and the next request
/// reads Valkey: the bound is 5 s after the cell stored the revocation (plus the link's delivery time before that).
/// </para>
/// <para>
/// <b>Valkey down: fail closed, with a short grace.</b> A read that fails is answered from an expired "not revoked"
/// entry for up to <see cref="RealtimeOptions.RevocationStaleTolerance"/> more (30 s), so a restart of Valkey does not
/// reject people. After that, and for a session never seen, the verdict is <see cref="RevocationVerdict.Unavailable"/>: the
/// caller answers 503 (never 401: a client treats 401 as "sign in again"). During the outage no revocation can be stored either
/// (the connector retries), so failing open would let a revoked session through for the whole outage; the cost of failing closed is
/// that the cell's API is unavailable while Valkey is, which it largely is anyway (live events and the backplane need it).
/// A failed read is quick: the connection's backlog policy is fail-fast and commands time out after
/// <see cref="RealtimeOptions.CommandTimeout"/>.
/// </para>
/// </remarks>
internal sealed partial class RevocationChecker(IRevocationStore store, IOptions<RealtimeOptions> options, TimeProvider timeProvider, ILogger<RevocationChecker> logger)
{
    // Revocations in force (few: the sign-outs of the last minutes) and clear answers (one or more per active session) live apart,
    // so a flood of clear answers can never push a revocation out. Both are concurrent maps: a lookup takes no lock.
    private readonly ConcurrentDictionary<string, Entry> _revoked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _clear = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _clearOrder = new();
    private long _lastFailureTicks;
    private int _evicting;

    /// <summary>Entries held now (revoked and clear); for tests and diagnostics.</summary>
    internal int Count => _revoked.Count + _clear.Count;

    /// <summary>Clear answers held now; for tests.</summary>
    internal int ClearCount => _clear.Count;

    /// <summary>
    /// Checks a token's session id (<c>sid</c>), account (<c>sub</c>) and issue time (<c>iat</c>). Never throws for a Valkey failure; it
    /// answers <see cref="RevocationVerdict.Unavailable"/>.
    /// </summary>
    public async ValueTask<RevocationVerdict> CheckAsync(string? sessionId, Guid accountId, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        // A session id too long to have been stored cannot be revoked by id; the account check still applies.
        var sid = sessionId is { Length: > 0 and <= ValkeyRevocationStore.MaxSessionIdLength } ? sessionId : null;
        var sidKey = sid is null ? null : "s:" + accountId.ToString("N") + ":" + sid;
        var accountKey = "a:" + accountId.ToString("N");

        var now = timeProvider.GetUtcNow();
        var (sidEntry, sidUsable) = Look(sidKey, now, allowStale: false);
        var (accountEntry, accountUsable) = Look(accountKey, now, allowStale: false);

        // A revocation in force needs no Valkey: it is refused whatever the other lookups say.
        if (Decide(sidEntry, accountEntry, issuedAt, now) == RevocationVerdict.Revoked)
        {
            return RevocationVerdict.Revoked;
        }

        if (sidUsable && accountUsable)
        {
            return RevocationVerdict.Valid;
        }

        if (await TryReadAsync(sid, sidKey, accountKey, accountId, cancellationToken))
        {
            now = timeProvider.GetUtcNow();
            sidEntry = Look(sidKey, now, allowStale: false).Entry;
            accountEntry = Look(accountKey, now, allowStale: false).Entry;
            return Decide(sidEntry, accountEntry, issuedAt, now);
        }

        // Fail closed, except for a clear answer that is only a little old.
        (sidEntry, sidUsable) = Look(sidKey, now, allowStale: true);
        (accountEntry, accountUsable) = Look(accountKey, now, allowStale: true);
        return sidUsable && accountUsable ? Decide(sidEntry, accountEntry, issuedAt, now) : RevocationVerdict.Unavailable;
    }

    // Reads the store and caches what it holds; false when it cannot be read (and, for a moment after a failure, without asking).
    [SuppressMessage("Design", "CA1031", Justification = "Any failure to reach Valkey is the same answer: the store is unavailable.")]
    private async Task<bool> TryReadAsync(string? sid, string? sidKey, string accountKey, Guid accountId, CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastFailureTicks) < options.Value.RevocationOutageBackoff.Ticks)
        {
            return false;
        }

        try
        {
            var (session, account) = await store.GetAsync(sid, accountId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (sidKey is not null)
            {
                _ = Remember(sidKey, session, now);
            }

            _ = Remember(accountKey, account, now);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _lastFailureTicks, timeProvider.GetUtcNow().UtcTicks);
            LogUnavailable(logger, exception.GetType().Name);
            return false;
        }
    }

    // Revoked when the session is, or the account is and the token was issued up to the second of the revocation (iat has second
    // precision; a sign-in after it is allowed).
    private static RevocationVerdict Decide(Entry? sidEntry, Entry? accountEntry, DateTimeOffset issuedAt, DateTimeOffset now) =>
        sidEntry is { Revoked: true } sid && now < sid.KeepUntil
            || accountEntry is { Revoked: true } account && now < account.KeepUntil
                && issuedAt.ToUnixTimeSeconds() <= account.RevokedAt.ToUnixTimeSeconds()
            ? RevocationVerdict.Revoked
            : RevocationVerdict.Valid;

    /// <summary>Takes a revocation this node was told about (applied here or announced by another node) into the cache at once.</summary>
    public void Learn(SessionRevocation revocation)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        var key = revocation.SessionId is { } sessionId ? "s:" + revocation.AccountId.ToString("N") + ":" + sessionId : "a:" + revocation.AccountId.ToString("N");
        var now = timeProvider.GetUtcNow();
        if (revocation.KeepUntil <= now)
        {
            return;
        }

        _ = PutRevoked(key, new Entry(Revoked: true, revocation.RevokedAt, revocation.KeepUntil, now), now);
    }

    // The entry for the key, and whether it can be used as it is: a revoked one until keep_until, a clear one while fresh
    // (or, when stale answers are allowed, a little longer).
    private (Entry? Entry, bool Usable) Look(string? key, DateTimeOffset now, bool allowStale)
    {
        if (key is null)
        {
            return (null, true);
        }

        if (_revoked.TryGetValue(key, out var revoked))
        {
            if (now < revoked.KeepUntil)
            {
                return (revoked, true);
            }

            _ = _revoked.TryRemove(new KeyValuePair<string, Entry>(key, revoked));
        }

        if (!_clear.TryGetValue(key, out var entry))
        {
            return (null, false);
        }

        var settings = options.Value;
        var limit = allowStale ? settings.RevocationCacheTtl + settings.RevocationStaleTolerance : settings.RevocationCacheTtl;
        return now - entry.CheckedAt < limit ? (entry, true) : (null, false);
    }

    private Entry Remember(string key, RevocationRecord? record, DateTimeOffset now)
    {
        if (record is { } held)
        {
            return PutRevoked(key, new Entry(Revoked: true, held.RevokedAt, held.KeepUntil, now), now);
        }

        // A revocation this node already knows is never forgotten because Valkey does not hold it (it was cut short).
        if (_revoked.TryGetValue(key, out var known) && now < known.KeepUntil)
        {
            return known;
        }

        var entry = new Entry(Revoked: false, default, default, now);
        if (_clear.TryAdd(key, entry))
        {
            _clearOrder.Enqueue(key);
            EvictClear();
        }
        else
        {
            _clear[key] = entry;
        }

        return entry;
    }

    private Entry PutRevoked(string key, Entry entry, DateTimeOffset now)
    {
        if (_revoked.Count >= options.Value.RevocationCacheMaxEntries && !_revoked.ContainsKey(key))
        {
            // Only expired revocations are dropped; one in force is never evicted. A flood beyond the limit (not a thing sign-outs do)
            // is left to Valkey, which still refuses the token on the next read.
            foreach (var pair in _revoked.Where(pair => now >= pair.Value.KeepUntil))
            {
                _ = _revoked.TryRemove(pair);
            }

            if (_revoked.Count >= options.Value.RevocationCacheMaxEntries)
            {
                return entry;
            }
        }

        return _revoked.AddOrUpdate(key, entry, (_, existing) => existing.KeepUntil >= entry.KeepUntil ? existing : entry);
    }

    // Over the limit, the oldest clear answers go first (a miss costs one read); revocations and recent answers stay. One thread
    // evicts at a time; the others carry on, and the next insert catches up.
    private void EvictClear()
    {
        var max = options.Value.RevocationCacheMaxEntries;
        if (_clear.Count <= max || Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (_clear.Count > max && _clearOrder.TryDequeue(out var oldest))
            {
                _ = _clear.TryRemove(oldest, out _);
            }
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "The revocation check could not reach Valkey ({ExceptionType}); until it can, requests without a recent answer are refused (or served, when the cell fails open).")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType);

    private readonly record struct Entry(bool Revoked, DateTimeOffset RevokedAt, DateTimeOffset KeepUntil, DateTimeOffset CheckedAt);
}
