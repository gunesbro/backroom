namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The connections of this node, by id and by account. A node only knows its own connections, which is why removing an
/// account from an organization's groups goes to every node (<see cref="GroupRemovalChannel"/>). Entries exist only while
/// a connection is open, and an account holds at most <c>MaxConnectionsPerAccount</c>, so the registry is bounded by
/// the node's connections.
/// </summary>
internal sealed class ConnectionRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ConnectionState> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<ConnectionState>> _byAccount = [];

    /// <summary>Registers the connection; <see langword="null"/> when the account already holds <paramref name="maxPerAccount"/>.</summary>
    public ConnectionState? TryAdd(string connectionId, Guid accountId, int maxPerAccount)
    {
        lock (_lock)
        {
            if (_connections.ContainsKey(connectionId))
            {
                return null;
            }

            if (!_byAccount.TryGetValue(accountId, out var connections))
            {
                connections = [];
                _byAccount[accountId] = connections;
            }

            if (connections.Count >= maxPerAccount)
            {
                return null;
            }

            var state = new ConnectionState(connectionId, accountId);
            connections.Add(state);
            _connections[connectionId] = state;
            return state;
        }
    }

    public ConnectionState? Find(string connectionId)
    {
        lock (_lock)
        {
            return _connections.GetValueOrDefault(connectionId);
        }
    }

    public ConnectionState? Remove(string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.Remove(connectionId, out var state))
            {
                return null;
            }

            if (_byAccount.TryGetValue(state.AccountId, out var connections))
            {
                connections.Remove(state);
                if (connections.Count == 0)
                {
                    _byAccount.Remove(state.AccountId);
                }
            }

            return state;
        }
    }

    /// <summary>A snapshot of the account's connections on this node.</summary>
    public IReadOnlyList<ConnectionState> ConnectionsOf(Guid accountId)
    {
        lock (_lock)
        {
            return _byAccount.TryGetValue(accountId, out var connections) ? [.. connections] : [];
        }
    }
}
