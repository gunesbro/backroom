namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>One open link stream; <see cref="Ended"/> fires on expiry, abort, supersession or an epoch change.</summary>
internal sealed class CellLinkSession : IDisposable
{
    private readonly CellLinkSessions _owner;

    // Never disposed: they have no timer or wait handle, and a newer session or an epoch increment may cancel
    // them at any time, also after this one is disposed.
    private readonly CancellationTokenSource _superseded = new();
    private readonly CancellationTokenSource _epochChanged = new();
    private readonly CancellationTokenSource _expired;
    private readonly CancellationTokenSource _ended;

    internal CellLinkSession(CellLinkSessions owner, string cellId, TimeSpan lifetime, TimeProvider timeProvider, CancellationToken callAborted)
    {
        _owner = owner;
        CellId = cellId;
        _expired = new CancellationTokenSource(lifetime, timeProvider);
        _ended = CancellationTokenSource.CreateLinkedTokenSource(callAborted, _superseded.Token, _epochChanged.Token, _expired.Token);
    }

    public string CellId { get; }

    public CancellationToken Ended => _ended.Token;

    public bool IsSuperseded => _superseded.IsCancellationRequested;

    /// <summary>The cell's epoch was incremented while this stream was open (docs/adr/0011, "Cell epoch").</summary>
    public bool IsEpochChanged => _epochChanged.IsCancellationRequested;

    public bool IsExpired => _expired.IsCancellationRequested;

    internal void Supersede() => _superseded.Cancel();

    internal void ChangeEpoch() => _epochChanged.Cancel();

    public void Dispose()
    {
        _owner.Remove(this);
        _ended.Dispose();
        _expired.Dispose();
    }
}
