namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>One open link stream; <see cref="Ended"/> fires on expiry, abort or supersession.</summary>
internal sealed class CellLinkSession : IDisposable
{
    private readonly CellLinkSessions _owner;

    // Never disposed: it has no timer or wait handle, and a newer session may cancel it at any time,
    // also after this one is disposed.
    private readonly CancellationTokenSource _superseded = new();
    private readonly CancellationTokenSource _expired;
    private readonly CancellationTokenSource _ended;

    internal CellLinkSession(CellLinkSessions owner, string cellId, TimeSpan lifetime, TimeProvider timeProvider, CancellationToken callAborted)
    {
        _owner = owner;
        CellId = cellId;
        _expired = new CancellationTokenSource(lifetime, timeProvider);
        _ended = CancellationTokenSource.CreateLinkedTokenSource(callAborted, _superseded.Token, _expired.Token);
    }

    public string CellId { get; }

    public CancellationToken Ended => _ended.Token;

    public bool IsSuperseded => _superseded.IsCancellationRequested;

    public bool IsExpired => _expired.IsCancellationRequested;

    internal void Supersede() => _superseded.Cancel();

    public void Dispose()
    {
        _owner.Remove(this);
        _ended.Dispose();
        _expired.Dispose();
    }
}
