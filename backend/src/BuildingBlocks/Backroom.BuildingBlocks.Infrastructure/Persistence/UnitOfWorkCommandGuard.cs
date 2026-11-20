using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Lets a module DbContext send commands only inside the active unit of work, scoped to the
/// organization it began with. Only the unit of work opens the connection: if EF Core tries to (the
/// unit of work has not begun, has ended, or the connection broke), it fails instead of running
/// commands outside the transaction that carries the organization setting.
/// </summary>
internal sealed class UnitOfWorkCommandGuard(DatabaseSession session) : DbCommandInterceptor, IDbConnectionInterceptor
{
    public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
    {
        session.EnsureActive();
        return result;
    }

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        throw ConnectionNotOpen();

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        throw ConnectionNotOpen();

    private InvalidOperationException ConnectionNotOpen()
    {
        // Reports "not begun", "completed" or "organization changed" when one of those is the cause.
        session.EnsureActive();
        return new InvalidOperationException("The unit of work's connection is not open; the connection may have been lost.");
    }
}
