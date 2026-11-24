namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// What this node knows about one hub connection: the account and the groups it joined, with the organization of each,
/// so a membership ending can remove exactly that organization's groups. The state lives and dies with the connection
/// and holds at most <c>MaxSubscriptionsPerConnection</c> names.
/// </summary>
/// <remarks>
/// <see cref="Gate"/> serializes subscribing, unsubscribing and removal for this connection. A subscribe holds it from
/// its membership check to the moment the group is joined, so a removal (which needs the gate) that follows the end of
/// the membership always finds the group and removes it: no connection keeps a group after a removal.
/// </remarks>
internal sealed class ConnectionState(string connectionId, Guid accountId)
{
    private readonly Dictionary<string, Guid> _groups = new(StringComparer.Ordinal);

    public string ConnectionId { get; } = connectionId;

    public Guid AccountId { get; } = accountId;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The token's session id (<c>sid</c>), or <see langword="null"/> when it carries none; set once at connect.</summary>
    public string? SessionId { get; private set; }

    /// <summary>When the connection's token was issued (<c>iat</c>); a revocation of every session of the account closes the connections whose token is not newer.</summary>
    public DateTimeOffset IssuedAt { get; private set; }

    private Action? _abort;

    /// <summary>Records what a revocation needs to find and close the connection: its session, token issue time and the hub's abort.</summary>
    public void Bind(string? sessionId, DateTimeOffset issuedAt, Action abort)
    {
        SessionId = sessionId;
        IssuedAt = issuedAt;
        _abort = abort;
    }

    /// <summary>Closes the connection (the client reconnects, and its token is checked again).</summary>
    public void Abort() => _abort?.Invoke();

    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

    /// <summary>Under the gate: the presence groups of the connection's watch set (a subset of its groups).</summary>
    public IReadOnlySet<string> Watched => _watched;

    /// <summary>Under the gate: joins a presence group of the watch set (counted among the groups).</summary>
    public void AddWatched(string group, Guid organizationId)
    {
        _groups[group] = organizationId;
        _watched.Add(group);
    }

    /// <summary>
    /// Counts an activity report: <see langword="false"/> when the last one accepted was less than <paramref name="interval"/> ago (the
    /// report is dropped). A connection's hub calls run one at a time (one invocation per client), so no lock is needed.
    /// </summary>
    public bool TryCountActivity(DateTimeOffset now, TimeSpan interval)
    {
        if (now - _lastActivity < interval)
        {
            return false;
        }

        _lastActivity = now;
        return true;
    }

    private DateTimeOffset _windowStart;
    private int _calls;
    private int _refusals;

    /// <summary>Under the gate: counts a subscribe call in the current window; <see langword="false"/> when it is over the limit.</summary>
    public bool TryCountCall(DateTimeOffset now, TimeSpan window, int max)
    {
        if (_calls == 0 || now - _windowStart >= window)
        {
            _windowStart = now;
            _calls = 0;
        }

        _calls++;
        return _calls <= max;
    }

    /// <summary>Under the gate: counts a refused subscribe call and returns how many this connection has had.</summary>
    public int CountRefusal() => ++_refusals;

    /// <summary>Set (under the gate) when the connection has ended; nothing joins a group afterwards.</summary>
    public bool Closed { get; set; }

    /// <summary>Under the gate.</summary>
    public int Count => _groups.Count;

    /// <summary>Under the gate.</summary>
    public bool Contains(string group) => _groups.ContainsKey(group);

    /// <summary>Under the gate.</summary>
    public void Add(string group, Guid organizationId) => _groups[group] = organizationId;

    /// <summary>Under the gate.</summary>
    public bool Remove(string group)
    {
        _watched.Remove(group);
        return _groups.Remove(group);
    }

    /// <summary>Under the gate: the groups of <paramref name="organizationId"/>.</summary>
    public List<string> GroupsOf(Guid organizationId)
    {
        var groups = new List<string>();
        foreach (var (group, organization) in _groups)
        {
            if (organization == organizationId)
            {
                groups.Add(group);
            }
        }

        return groups;
    }
}
