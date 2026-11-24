using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// The app node's side of automatic presence (docs/contracts/phase-2.md, flow 9). It knows this node's hub connections: the account,
/// when the person last used the client and whether the client reported them idle, and whom the connection watches. Every change is
/// written to Valkey and evaluated at once; <see cref="PresenceSweeper"/> renews the connections and evaluates what becomes due by
/// itself (idle after <see cref="PresenceOptions.IdleAfter"/>, offline <see cref="PresenceOptions.OfflineAfter"/> after the last
/// connection, coalesced events). The local state lives only while a connection is open and is bounded by
/// <see cref="PresenceOptions.MaxConnections"/>. Valkey failures are logged with ids and counted; they never reach the hub.
/// </summary>
internal sealed partial class PresenceTracker(
    PresenceStore store,
    PresencePublisher publisher,
    IOptions<PresenceOptions> options,
    TimeProvider timeProvider,
    ILogger<PresenceTracker> logger) : IPresenceTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Tracked> _connections = new(StringComparer.Ordinal);

    public bool Enabled => options.Value.Enabled;

    public async Task ConnectedAsync(string connectionId, Guid accountId, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var tracked = new Tracked(accountId) { LastActiveAt = now };
        lock (_lock)
        {
            if (_connections.Count >= options.Value.MaxConnections)
            {
                LogFull(logger, accountId);
                return;
            }

            _connections[connectionId] = tracked;
        }

        // Connecting is using the client: the person is online.
        await WriteAsync(tracked, connectionId, now + options.Value.OfflineAfter, now, idle: false, "connect", cancellationToken);
    }

    public async Task ActivityAsync(string connectionId, bool active, CancellationToken cancellationToken)
    {
        if (!Enabled || Find(connectionId) is not { } tracked)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        DateTimeOffset lastActive;
        lock (_lock)
        {
            if (active)
            {
                tracked.LastActiveAt = now;
            }

            tracked.Idle = !active;
            lastActive = tracked.LastActiveAt;
        }

        await WriteAsync(tracked, connectionId, now + options.Value.OfflineAfter, lastActive, !active, "activity", cancellationToken);
    }

    public async Task DisconnectedAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        Tracked? tracked;
        lock (_lock)
        {
            _connections.Remove(connectionId, out tracked);
        }

        if (tracked is null)
        {
            return;
        }

        // The field is left to expire after the grace: a reload or a short network loss does not show the person offline.
        var now = timeProvider.GetUtcNow();
        await WriteAsync(tracked, connectionId, now + options.Value.OfflineAfter, tracked.LastActiveAt, tracked.Idle, "disconnect", cancellationToken);
    }

    public async Task WatchAsync(string connectionId, Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        if (!Enabled || Find(connectionId) is not { } tracked)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            tracked.WatchOrganization = organizationId;
            tracked.Watched = [.. accountIds];
            tracked.WatchRenewedAt = now;
        }

        try
        {
            await store.WatchAsync(organizationId, accountIds, now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            PresenceTelemetry.Failures.Add(1, new KeyValuePair<string, object?>("operation", "watch"));
            LogFailed(logger, "watch", tracked.AccountId, exception.GetType().Name);
        }
    }

    /// <summary>A snapshot of this node's connections for the sweeper's renewal.</summary>
    internal List<(Guid AccountId, string ConnectionId, long LastActiveAtMs, bool Idle)> Snapshot()
    {
        lock (_lock)
        {
            var snapshot = new List<(Guid, string, long, bool)>(_connections.Count);
            foreach (var (connectionId, tracked) in _connections)
            {
                snapshot.Add((tracked.AccountId, connectionId, tracked.LastActiveAt.ToUnixTimeMilliseconds(), tracked.Idle));
            }

            return snapshot;
        }
    }

    /// <summary>The watches renewed last before <paramref name="before"/>; their renewal time is set to <paramref name="now"/>.</summary>
    internal List<(Guid OrganizationId, Guid[] AccountIds)> TakeWatchesToRenew(DateTimeOffset before, DateTimeOffset now)
    {
        lock (_lock)
        {
            var due = new List<(Guid, Guid[])>();
            foreach (var tracked in _connections.Values)
            {
                if (tracked.Watched.Length > 0 && tracked.WatchRenewedAt <= before)
                {
                    tracked.WatchRenewedAt = now;
                    due.Add((tracked.WatchOrganization, tracked.Watched));
                }
            }

            return due;
        }
    }

    private Tracked? Find(string connectionId)
    {
        lock (_lock)
        {
            return _connections.GetValueOrDefault(connectionId);
        }
    }

    private async Task WriteAsync(Tracked tracked, string connectionId, DateTimeOffset expiresAt, DateTimeOffset lastActiveAt, bool idle, string operation, CancellationToken cancellationToken)
    {
        try
        {
            await store.SetConnectionAsync(
                tracked.AccountId, connectionId, expiresAt.ToUnixTimeMilliseconds(), lastActiveAt.ToUnixTimeMilliseconds(), idle, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            PresenceTelemetry.Failures.Add(1, new KeyValuePair<string, object?>("operation", operation));
            LogFailed(logger, operation, tracked.AccountId, exception.GetType().Name);
            return;
        }

        await publisher.EvaluateAsync(tracked.AccountId, cancellationToken);
    }

    /// <summary>One connection; changed under the tracker's lock.</summary>
    private sealed class Tracked(Guid accountId)
    {
        public Guid AccountId { get; } = accountId;

        public DateTimeOffset LastActiveAt { get; set; }

        public bool Idle { get; set; }

        public Guid WatchOrganization { get; set; }

        public Guid[] Watched { get; set; } = [];

        public DateTimeOffset WatchRenewedAt { get; set; }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Presence {Operation} of account {AccountId} failed ({ExceptionType}).")]
    private static partial void LogFailed(ILogger logger, string operation, Guid accountId, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "A connection of account {AccountId} is not tracked for presence: the node tracks its limit of connections.")]
    private static partial void LogFull(ILogger logger, Guid accountId);
}
