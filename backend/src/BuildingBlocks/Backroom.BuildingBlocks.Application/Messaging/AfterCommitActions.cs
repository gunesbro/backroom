namespace Backroom.BuildingBlocks.Application.Messaging;

/// <summary>
/// Work an integration event handler wants done only after its unit of work has committed (live events, which are never sent
/// for state that was rolled back). The consumer host runs the actions after <c>CommitAsync</c>; a handler that throws, a
/// redelivery skipped by the inbox or a failed commit runs none. Actions are best effort: one that throws is skipped and never
/// fails the delivery, so each handles and logs its own failure (ids only).
/// </summary>
public sealed class AfterCommitActions
{
    private readonly List<Func<CancellationToken, Task>> _actions = [];

    public void Add(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Add(action);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var actions = _actions.ToArray();
        _actions.Clear();
        foreach (var action in actions)
        {
            try
            {
                await action(cancellationToken);
            }
#pragma warning disable CA1031 // Best effort: the state is committed, a failed announcement must not redeliver the event.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // Skipped on purpose; the action logs what it can (the exception text could carry personal data).
            }
        }
    }
}
