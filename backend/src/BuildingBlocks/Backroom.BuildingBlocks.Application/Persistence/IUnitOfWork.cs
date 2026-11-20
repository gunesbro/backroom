namespace Backroom.BuildingBlocks.Application.Persistence;

/// <summary>
/// The database transaction of the current request or message handler: every one runs in a single
/// transaction (docs/contracts/phase-1.md). The pipeline begins it after the organization context is
/// set and commits it when the operation succeeds; ending the scope without a commit rolls it back.
/// No external calls are made inside it: side effects go through the outbox. A database error aborts
/// the transaction, so after one the handler returns an error instead of sending more commands.
/// </summary>
public interface IUnitOfWork
{
    Task BeginAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
