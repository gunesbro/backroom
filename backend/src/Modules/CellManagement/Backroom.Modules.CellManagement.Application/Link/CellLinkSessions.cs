namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>
/// The open link stream of each cell in this control-plane instance. A new stream supersedes the
/// cell's previous one (docs/contracts/phase-1.md, "Stream and identity"). At most one entry per
/// registered cell.
/// </summary>
internal sealed class CellLinkSessions(TimeProvider timeProvider)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CellLinkSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens the cell's session, ending at <paramref name="expiresAt"/> (the token's expiry), when the
    /// call is aborted, or when a newer session supersedes it.
    /// </summary>
    public CellLinkSession Open(string cellId, DateTimeOffset expiresAt, CancellationToken callAborted)
    {
        var lifetime = expiresAt - timeProvider.GetUtcNow();
        var session = new CellLinkSession(this, cellId, lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero, timeProvider, callAborted);
        CellLinkSession? previous;
        lock (_lock)
        {
            _sessions.TryGetValue(cellId, out previous);
            _sessions[cellId] = session;
        }

        // Outside the lock: cancelling runs the old stream's callbacks.
        previous?.Supersede();
        return session;
    }

    public bool IsConnected(string cellId)
    {
        lock (_lock)
        {
            return _sessions.ContainsKey(cellId);
        }
    }

    internal void Remove(CellLinkSession session)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.CellId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.CellId);
            }
        }
    }
}
