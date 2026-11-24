namespace Backroom.Modules.Presence.Contracts;

/// <summary>
/// The hub's side of automatic presence (app role): connections, the activity clients report and the people they watch. Calls never
/// throw for Valkey failures (presence is best effort, the connection must not fail for it); failures are logged and counted.
/// </summary>
public interface IPresenceTracker
{
    /// <summary>Whether presence is on (<c>Features:Presence</c>); when off, every call does nothing and the hub refuses the methods.</summary>
    bool Enabled { get; }

    Task ConnectedAsync(string connectionId, Guid accountId, CancellationToken cancellationToken);

    /// <summary><paramref name="active"/>: the person used the client; otherwise the client reports them idle.</summary>
    Task ActivityAsync(string connectionId, bool active, CancellationToken cancellationToken);

    /// <summary>The connection ended; the person stays as they were until the grace (60 s) without any connection has passed.</summary>
    Task DisconnectedAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// The connection now watches <paramref name="accountIds"/> in <paramref name="organizationId"/> (all checked to be active members
    /// there by the caller): changes of those people are published to that organization's presence groups.
    /// </summary>
    Task WatchAsync(string connectionId, Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
