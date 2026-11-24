using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure.Revocation;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// The revocation check's cache and its fail-closed rule (docs/contracts/phase-1.md, "Identity and tokens"), on a fake clock and a fake
/// store: how often Valkey is asked, how long a revocation takes to be seen, and what happens when Valkey cannot answer.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class RevocationCheckerTests
{
    private static readonly DateTimeOffset s_start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(s_start);
    private readonly FakeStore _store = new();
    private readonly RealtimeOptions _options = new() { CellId = "a", RevocationCacheMaxEntries = 16 };
    private readonly Guid _account = Guid.NewGuid();

    private RevocationChecker Checker() => new(_store, Options.Create(_options), _time, NullLogger<RevocationChecker>.Instance);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Check_WhenNothingIsRevoked_AsksTheStoreOncePerTtlAsync()
    {
        var checker = Checker();

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid);
        for (var request = 0; request < 50; request++)
        {
            (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid);
        }

        _store.Reads.Should().Be(1, "a request must not cost a Valkey round trip");

        _time.Advance(_options.RevocationCacheTtl - TimeSpan.FromMilliseconds(1));
        await CheckAsync(checker, "s1");
        _store.Reads.Should().Be(1);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await CheckAsync(checker, "s1");
        _store.Reads.Should().Be(2, "after the TTL the next request asks again");
    }

    [Fact]
    public async Task Check_WhenTheSessionIsRevokedAfterAClearAnswer_IsSeenWithinTheTtlAsync()
    {
        var checker = Checker();
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid);

        // This node missed the broadcast; the cell stored the revocation.
        _store.Sessions["s1"] = new RevocationRecord(_time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5));

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid, "within the TTL the clear answer still stands");
        _time.Advance(_options.RevocationCacheTtl);
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Revoked, "the bound is the cache TTL (5 seconds)");
        _options.RevocationCacheTtl.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Check_WhenTheNodeIsToldOfTheRevocation_RefusesAtOnceAsync()
    {
        var checker = Checker();
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid);

        checker.Learn(new SessionRevocation(_account, "s1", _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5)));

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Revoked);
        (await CheckAsync(checker, "other")).Should().Be(RevocationVerdict.Valid, "another session of the account is not revoked");
        _store.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Check_ForARevokedSession_KeepsRefusingFromTheCacheUntilKeepUntilAsync()
    {
        _store.Sessions["s1"] = new RevocationRecord(_time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5));
        var checker = Checker();

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Revoked);
        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        _store.Sessions.Clear();
        _store.Down = true;
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Revoked, "a revocation never un-revokes, and needs no Valkey to be enforced");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        _store.Down = false;
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid, "every token that could carry it has expired");
    }

    [Fact]
    public async Task Check_WhenValkeyForgetsARevocationThisNodeKnows_StillRefusesItAsync()
    {
        var checker = Checker();
        checker.Learn(new SessionRevocation(_account, "s1", _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5)));
        _time.Advance(TimeSpan.FromMinutes(1));

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Revoked, "an emptied Valkey must not un-revoke");
    }

    [Fact]
    public async Task Check_ForARevocationOfEverySession_RefusesTokensIssuedUpToItButNotAfterAsync()
    {
        var revokedAt = _time.GetUtcNow();
        _store.Accounts[_account] = new RevocationRecord(revokedAt, revokedAt.AddMinutes(5));
        var checker = Checker();

        (await checker.CheckAsync("s-old", _account, revokedAt.AddMinutes(-3), CancellationToken)).Should().Be(RevocationVerdict.Revoked);
        (await checker.CheckAsync("s-same-second", _account, revokedAt, CancellationToken)).Should().Be(RevocationVerdict.Revoked);
        (await checker.CheckAsync("s-new", _account, revokedAt.AddSeconds(1), CancellationToken)).Should().Be(RevocationVerdict.Valid, "a sign-in after the revocation is allowed");
        (await checker.CheckAsync(null, Guid.NewGuid(), revokedAt.AddMinutes(-3), CancellationToken)).Should().Be(RevocationVerdict.Valid, "another account");
    }

    [Fact]
    public async Task Check_WithATokenWithoutASessionId_ChecksTheAccountOnlyAsync()
    {
        var revokedAt = _time.GetUtcNow();
        _store.Accounts[_account] = new RevocationRecord(revokedAt, revokedAt.AddMinutes(5));
        var checker = Checker();

        (await checker.CheckAsync(null, _account, revokedAt.AddMinutes(-1), CancellationToken)).Should().Be(RevocationVerdict.Revoked);
        (await checker.CheckAsync(new string('s', 129), _account, revokedAt.AddMinutes(1), CancellationToken)).Should().Be(RevocationVerdict.Valid, "an id too long to have been stored is not looked up");
        _store.RequestedSessionIds.Should().NotContain(id => id != null && id.Length > 128);
    }

    [Fact]
    public async Task Check_WhenValkeyIsDownAndNothingIsCached_FailsClosedAsync()
    {
        _store.Down = true;
        var checker = Checker();

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Unavailable);
    }

    [Fact]
    public async Task Check_WhenValkeyGoesDown_UsesARecentClearAnswerForAWhileThenFailsClosedAsync()
    {
        var checker = Checker();
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid);
        _store.Down = true;

        _time.Advance(_options.RevocationCacheTtl);
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid, "a short outage (a Valkey restart) does not reject people");
        _time.Advance(_options.RevocationStaleTolerance - TimeSpan.FromMilliseconds(1));
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Unavailable, "past the tolerance the check fails closed");
        _options.RevocationStaleTolerance.Should().Be(TimeSpan.FromSeconds(30));

        _store.Down = false;
        _time.Advance(_options.RevocationOutageBackoff);
        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Valid, "it recovers as soon as Valkey answers");
    }

    [Fact]
    public async Task Check_AfterAFailedRead_DoesNotAskTheStoreAgainDuringTheBackoffAsync()
    {
        _store.Down = true;
        var checker = Checker();

        for (var request = 0; request < 20; request++)
        {
            (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Unavailable);
        }

        _store.Reads.Should().Be(1, "a hung Valkey must cost one wait per backoff window, not one per request");
        _time.Advance(_options.RevocationOutageBackoff);
        await CheckAsync(checker, "s1");
        _store.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Check_WhenValkeyIsDown_StillRefusesARevokedSessionAsync()
    {
        var checker = Checker();
        checker.Learn(new SessionRevocation(_account, "s1", _time.GetUtcNow(), _time.GetUtcNow().AddMinutes(5)));
        _store.Down = true;

        (await CheckAsync(checker, "s1")).Should().Be(RevocationVerdict.Revoked);
    }

    [Fact]
    public async Task Check_ForManySessions_KeepsTheCacheWithinItsLimitAsync()
    {
        var checker = Checker();

        for (var session = 0; session < 200; session++)
        {
            await checker.CheckAsync("s" + session, Guid.NewGuid(), s_start, CancellationToken);
            checker.Count.Should().BeLessThanOrEqualTo(_options.RevocationCacheMaxEntries);
        }
    }

    [Fact]
    public async Task Check_WhenTheCallerGivesUp_DoesNotFailClosedAsync()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _store.Down = true;
        _store.ThrowCancelled = true;
        var checker = Checker();

        var check = async () => await checker.CheckAsync("s1", _account, s_start, cancelled.Token);

        await check.Should().ThrowAsync<OperationCanceledException>("a request that was aborted is not a Valkey outage");
    }

    private ValueTask<RevocationVerdict> CheckAsync(RevocationChecker checker, string sessionId) =>
        checker.CheckAsync(sessionId, _account, s_start, CancellationToken);

    private sealed class FakeStore : IRevocationStore
    {
        public Dictionary<string, RevocationRecord> Sessions { get; } = [];

        public Dictionary<Guid, RevocationRecord> Accounts { get; } = [];

        public List<string?> RequestedSessionIds { get; } = [];

        public bool Down { get; set; }

        public bool ThrowCancelled { get; set; }

        public int Reads { get; private set; }

        public Task StoreAsync(SessionRevocation revocation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(RevocationRecord? Session, RevocationRecord? Account)> GetAsync(string? sessionId, Guid accountId, CancellationToken cancellationToken)
        {
            if (ThrowCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Reads++;
            RequestedSessionIds.Add(sessionId);
            if (Down)
            {
                throw new TimeoutException("Valkey is down.");
            }

            RevocationRecord? session = sessionId is not null && Sessions.TryGetValue(sessionId, out var s) ? s : null;
            RevocationRecord? account = Accounts.TryGetValue(accountId, out var a) ? a : null;
            return Task.FromResult((session, account));
        }
    }
}
