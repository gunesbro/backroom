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
/// <b>Cache.</b> A small in-memory map in front of Valkey, bounded (<see cref="RealtimeOptions.RevocationCacheMaxEntries"/>;
/// a full one is emptied) and expiring: a "revoked" entry lasts until its <c>keep_until</c> (a revocation never un-revokes), a
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
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _lastFailureTicks;

    /// <summary>Entries held now; for tests and diagnostics.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

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

        _ = Put(key, new Entry(Revoked: true, revocation.RevokedAt, revocation.KeepUntil, now));
    }

    // The entry for the key, and whether it can be used as it is: a revoked one until keep_until, a clear one while fresh
    // (or, when stale answers are allowed, a little longer).
    private (Entry? Entry, bool Usable) Look(string? key, DateTimeOffset now, bool allowStale)
    {
        if (key is null)
        {
            return (null, true);
        }

        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out entry))
            {
                return (null, false);
            }
        }

        var settings = options.Value;
        if (entry.Revoked)
        {
            return now < entry.KeepUntil ? (entry, true) : (null, false);
        }

        var age = now - entry.CheckedAt;
        var limit = allowStale ? settings.RevocationCacheTtl + settings.RevocationStaleTolerance : settings.RevocationCacheTtl;
        return age < limit ? (entry, true) : (null, false);
    }

    private Entry Remember(string key, RevocationRecord? record, DateTimeOffset now)
    {
        // A revocation this node already knows is never forgotten because Valkey does not hold it (it was cut short).
        var entry = record is { } held
            ? new Entry(Revoked: true, held.RevokedAt, held.KeepUntil, now)
            : new Entry(Revoked: false, default, default, now);
        return Put(key, entry);
    }

    private Entry Put(string key, Entry entry)
    {
        lock (_lock)
        {
            if (_entries.Count >= options.Value.RevocationCacheMaxEntries && !_entries.ContainsKey(key))
            {
                Trim(entry.CheckedAt);
            }

            // Never replace a revoked entry with a clear one while the revocation is in force.
            if (!entry.Revoked && _entries.TryGetValue(key, out var existing) && existing.Revoked && entry.CheckedAt < existing.KeepUntil)
            {
                return existing;
            }

            _entries[key] = entry;
            return entry;
        }
    }

    // Under the lock: drop what is no longer needed; if the cache is still full, start again (a miss costs one read).
    private void Trim(DateTimeOffset now)
    {
        var settings = options.Value;
        var expired = _entries
            .Where(pair => pair.Value.Revoked ? now >= pair.Value.KeepUntil : now - pair.Value.CheckedAt >= settings.RevocationCacheTtl + settings.RevocationStaleTolerance)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }

        if (_entries.Count >= settings.RevocationCacheMaxEntries)
        {
            _entries.Clear();
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "The revocation check could not reach Valkey ({ExceptionType}); until it can, requests without a recent answer are refused (or served, when the cell fails open).")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType);

    private readonly record struct Entry(bool Revoked, DateTimeOffset RevokedAt, DateTimeOffset KeepUntil, DateTimeOffset CheckedAt);
}
