using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// Presence in Valkey (docs/contracts/phase-2.md, "Data": no PostgreSQL schema, owner decision 16), under
/// <c>backroom:cell:{cellId}:presence:</c> so cells that share a server stay apart. Per person: <c>c:{id}</c> the open connections
/// (hash, fields expire after <see cref="PresenceOptions.OfflineAfter"/> unless renewed), <c>p:{id}</c> what was last published,
/// <c>s:{id}</c> the account status copy (<c>status</c> in the contract), <c>w:{id}</c> the organizations where someone watches the person.
/// One <c>due</c> set holds who must be evaluated again and when. Every key expires, so nothing is kept as history: no daily totals, no
/// last-seen times beyond the connections that are open.
/// </summary>
internal sealed class PresenceStore(PresenceValkey valkey, IOptions<PresenceOptions> options)
{
    private readonly PresenceOptions _options = options.Value;

    private string Prefix => "backroom:cell:" + _options.CellId + ":presence:";

    private RedisKey Connections(Guid accountId) => Prefix + "c:" + accountId.ToString("N");

    private RedisKey Published(Guid accountId) => Prefix + "p:" + accountId.ToString("N");

    private RedisKey Status(Guid accountId) => Prefix + "s:" + accountId.ToString("N");

    private RedisKey Watches(Guid accountId) => Prefix + "w:" + accountId.ToString("N");

    private RedisKey Due => Prefix + "due";

    /// <summary>Writes one connection's field and makes the person due now (so the change is evaluated by the caller or a sweep).</summary>
    public async Task SetConnectionAsync(Guid accountId, string connectionId, long expiresAtMs, long lastActiveAtMs, bool idle, CancellationToken cancellationToken)
    {
        var database = await valkey.DatabaseAsync(cancellationToken);
        var batch = database.CreateBatch();
        var set = batch.HashSetAsync(Connections(accountId), connectionId, PresenceRules.Connection(expiresAtMs, lastActiveAtMs, idle));
        var expire = batch.KeyExpireAsync(Connections(accountId), DateTime.UnixEpoch.AddMilliseconds(expiresAtMs) + _options.OfflineAfter, ExpireWhen.Always);
        batch.Execute();
        await Task.WhenAll(set, expire);
    }

    /// <summary>Renews the fields of open connections (one round trip for the whole batch); no evaluation, nothing changes state.</summary>
    public async Task RefreshAsync(IReadOnlyList<(Guid AccountId, string ConnectionId, long LastActiveAtMs, bool Idle)> connections, long expiresAtMs, CancellationToken cancellationToken)
    {
        if (connections.Count == 0)
        {
            return;
        }

        var database = await valkey.DatabaseAsync(cancellationToken);
        var batch = database.CreateBatch();
        var tasks = new List<Task>(connections.Count * 2);
        var keyExpiry = DateTime.UnixEpoch.AddMilliseconds(expiresAtMs) + _options.OfflineAfter;
        foreach (var (accountId, connectionId, lastActiveAtMs, idle) in connections)
        {
            tasks.Add(batch.HashSetAsync(Connections(accountId), connectionId, PresenceRules.Connection(expiresAtMs, lastActiveAtMs, idle)));
            tasks.Add(batch.KeyExpireAsync(Connections(accountId), keyExpiry, ExpireWhen.Always));
        }

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    /// <summary>Evaluates the person (<see cref="PresenceScripts.Evaluate"/>); <see langword="null"/> when there is nothing to publish.</summary>
    public async Task<PresenceEvaluation?> EvaluateAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var database = await valkey.DatabaseAsync(cancellationToken);
        var result = await database.ScriptEvaluateAsync(
            PresenceScripts.Evaluate,
            [Connections(accountId), Published(accountId), Due, Status(accountId), Watches(accountId)],
            [
                accountId.ToString("N"),
                now.ToUnixTimeMilliseconds(),
                (long)_options.IdleAfter.TotalMilliseconds,
                (long)_options.EventInterval.TotalMilliseconds,
                (long)TimeSpan.FromDays(1).TotalMilliseconds,
                (long)_options.OfflineAfter.TotalMilliseconds,
                _options.MaxOrganizationsPerPerson,
            ]);
        var parts = (RedisResult[])result!;
        var code = (int)parts[0];
        if (code == 0)
        {
            return null;
        }

        var organizations = new List<Guid>();
        foreach (var organization in (RedisResult[])parts[1]!)
        {
            if (Guid.TryParseExact((string?)organization, "N", out var organizationId))
            {
                organizations.Add(organizationId);
            }
        }

        var status = (RedisResult[])parts[2]!;
        var nowMs = now.ToUnixTimeMilliseconds();
        return new PresenceEvaluation(
            accountId,
            (PresenceState)(code - 1),
            organizations,
            PresenceRules.DndUntil((string?)status[0], nowMs),
            PresenceRules.Custom((string?)status[1], (string?)status[2], (string?)status[3], nowMs));
    }

    /// <summary>The people due by <paramref name="now"/>, at most <paramref name="limit"/>, leased to the caller.</summary>
    public async Task<IReadOnlyList<Guid>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var database = await valkey.DatabaseAsync(cancellationToken);
        var result = await database.ScriptEvaluateAsync(
            PresenceScripts.Claim,
            [Due],
            [now.ToUnixTimeMilliseconds(), limit, (long)_options.SweepLease.TotalMilliseconds]);
        var accounts = new List<Guid>();
        foreach (var id in (RedisResult[])result!)
        {
            if (Guid.TryParseExact((string?)id, "N", out var accountId))
            {
                accounts.Add(accountId);
            }
        }

        return accounts;
    }

    /// <summary>Stores the copy if newer (<see cref="PresenceScripts.ApplyStatus"/>); returns whether it did.</summary>
    public async Task<bool> ApplyStatusAsync(AccountStatusCopy status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        var database = await valkey.DatabaseAsync(cancellationToken);
        var result = await database.ScriptEvaluateAsync(
            PresenceScripts.ApplyStatus,
            [Status(status.AccountId), Due],
            [
                status.Version,
                PresenceRules.Milliseconds(status.DndUntil),
                status.CustomEmoji ?? string.Empty,
                status.CustomText ?? string.Empty,
                PresenceRules.Milliseconds(status.CustomExpiresAt),
                status.TimeZone ?? string.Empty,
                (long)_options.StatusRetention.TotalMilliseconds,
                status.AccountId.ToString("N"),
                now.ToUnixTimeMilliseconds(),
            ]);
        return (int)result == 1;
    }

    /// <summary>Records that <paramref name="accountIds"/> are watched in <paramref name="organizationId"/> until the watch lifetime ends.</summary>
    public async Task WatchAsync(Guid organizationId, IReadOnlyCollection<Guid> accountIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return;
        }

        var database = await valkey.DatabaseAsync(cancellationToken);
        await database.ScriptEvaluateAsync(
            PresenceScripts.Watch,
            [.. accountIds.Select(Watches)],
            [
                organizationId.ToString("N"),
                (now + _options.WatchLifetime).ToUnixTimeMilliseconds(),
                now.ToUnixTimeMilliseconds(),
                (long)_options.WatchLifetime.TotalMilliseconds,
                _options.MaxOrganizationsPerPerson,
            ]);
    }

    /// <summary>Stops publishing the person's presence to the organization.</summary>
    public async Task ForgetAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken)
    {
        var database = await valkey.DatabaseAsync(cancellationToken);
        await database.SortedSetRemoveAsync(Watches(accountId), organizationId.ToString("N"));
    }

    /// <summary>What readers need of each person, in one round trip: the connection fields and the status values.</summary>
    public async Task<IReadOnlyList<(Guid AccountId, string?[] Connections, string?[] Status)>> ReadAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }

        var database = await valkey.DatabaseAsync(cancellationToken);
        var batch = database.CreateBatch();
        var reads = accountIds
            .Select(accountId => (
                AccountId: accountId,
                Connections: batch.HashValuesAsync(Connections(accountId)),
                Status: batch.HashGetAsync(Status(accountId), [(RedisValue)"dnd", (RedisValue)"emoji", (RedisValue)"text", (RedisValue)"exp"])))
            .ToList();
        batch.Execute();
        var result = new List<(Guid, string?[], string?[])>(reads.Count);
        foreach (var read in reads)
        {
            var connections = await read.Connections;
            var status = await read.Status;
            result.Add((read.AccountId, [.. connections.Select(value => (string?)value)], [.. status.Select(value => (string?)value)]));
        }

        return result;
    }
}
