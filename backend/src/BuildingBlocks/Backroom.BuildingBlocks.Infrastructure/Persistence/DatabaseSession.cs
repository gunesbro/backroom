using System.Globalization;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The unit of work: one connection and one transaction per request or message handler, shared by
/// every module's DbContext. When it begins, it writes the organization context into the
/// transaction-local setting <c>app.org_id</c> that the row-level security policies read; the setting
/// ends with the transaction, so it cannot leak to the next user of the pooled connection.
/// </summary>
/// <remarks>
/// DbContexts may be created before the unit of work begins (minimal APIs resolve handler parameters
/// before filters run); every command they send is checked by <see cref="UnitOfWorkCommandGuard"/>.
/// </remarks>
public sealed class DatabaseSession : IUnitOfWork, IAsyncDisposable
{
    // One statement, sent together with BEGIN. An empty value means "no organization": the policies
    // turn it into NULL, which matches no row.
    private const string SetOrganizationSql = "SELECT set_config('" + OrganizationRowLevelSecurity.SettingName + "', $1, true)";

    private readonly IOrganizationContext _organizationContext;
    private readonly DatabaseRoleGuard _roleGuard;
    private readonly NpgsqlConnection _connection;
    private NpgsqlTransaction? _transaction;
    private Guid? _organizationId;
    private SessionState _state;

    public DatabaseSession(NpgsqlDataSource dataSource, IOrganizationContext organizationContext, DatabaseRoleGuard roleGuard)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(organizationContext);
        ArgumentNullException.ThrowIfNull(roleGuard);
        _organizationContext = organizationContext;
        _roleGuard = roleGuard;

        // Not opened yet: no I/O and no pooled connection until BeginAsync.
        _connection = dataSource.CreateConnection();
    }

    private enum SessionState
    {
        NotStarted,
        Active,
        Completed,
    }

    /// <summary>The open connection of the active unit of work, for hand-written SQL.</summary>
    public NpgsqlConnection Connection
    {
        get
        {
            EnsureActive();
            return _connection;
        }
    }

    /// <summary>The transaction of the active unit of work.</summary>
    public NpgsqlTransaction Transaction
    {
        get
        {
            EnsureActive();
            return _transaction!;
        }
    }

    /// <summary>The connection object DbContexts are bound to; it is open only while the unit of work is active.</summary>
    internal NpgsqlConnection UnderlyingConnection => _connection;

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_state != SessionState.NotStarted)
        {
            throw new InvalidOperationException("The unit of work has already begun.");
        }

        var organizationId = _organizationContext.OrganizationId;
        await _connection.OpenAsync(cancellationToken);
        try
        {
            await _roleGuard.EnsureRoleCannotBypassRowSecurityAsync(_connection, cancellationToken);

            var transaction = await _connection.BeginTransactionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(SetOrganizationSql, _connection, transaction))
            {
                command.Parameters.Add(new NpgsqlParameter<string>
                {
                    TypedValue = organizationId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty,
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            _transaction = transaction;
            _organizationId = organizationId;
            _state = SessionState.Active;
        }
        catch
        {
            _state = SessionState.Completed;
            await ReleaseAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        _state = SessionState.Completed;
        try
        {
            await _transaction!.CommitAsync(cancellationToken);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    /// <summary>Rolls back the active unit of work; does nothing if none is active.</summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_state != SessionState.Active)
        {
            return;
        }

        _state = SessionState.Completed;
        try
        {
            await _transaction!.RollbackAsync(cancellationToken);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    /// <summary>Rolls back an uncommitted unit of work and returns the connection to the pool.</summary>
    public async ValueTask DisposeAsync()
    {
        _state = SessionState.Completed;
        await ReleaseAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Throws unless the unit of work is active and still scoped to the current organization context.
    /// The transaction was scoped to the organization known at <see cref="BeginAsync"/>; a later change
    /// would leave queries filtered by the old one, so it is an error rather than silently ignored.
    /// </summary>
    internal void EnsureActive()
    {
        switch (_state)
        {
            case SessionState.NotStarted:
                throw new InvalidOperationException("The unit of work has not begun; the pipeline begins it before handlers run.");
            case SessionState.Completed:
                throw new InvalidOperationException("The unit of work has already completed.");
        }

        if (_organizationContext.OrganizationId != _organizationId)
        {
            throw new InvalidOperationException("The organization context changed after the unit of work began.");
        }
    }

    private async ValueTask ReleaseAsync()
    {
        if (_transaction is not null)
        {
            // Disposing an uncommitted transaction rolls it back.
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        // Returns the physical connection to the pool as soon as the unit of work ends.
        await _connection.CloseAsync();
    }
}
