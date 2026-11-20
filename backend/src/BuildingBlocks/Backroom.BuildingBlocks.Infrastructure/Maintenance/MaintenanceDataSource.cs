using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// The maintenance role's own small connection pool. Owned by <see cref="MaintenanceJobRunner"/> and never
/// registered in dependency injection, so no request or handler can obtain a connection that bypasses
/// row-level security (checked by the architecture tests). Before first use it checks that the role is a
/// dedicated BYPASSRLS role: not a superuser, not an owner of database objects, no server privileges.
/// </summary>
internal sealed class MaintenanceDataSource : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private volatile bool _roleVerified;

    public MaintenanceDataSource(MaintenanceJobOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var connectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
            // Server error details can contain row values, that is personal data.
            IncludeErrorDetail = false,
            NoResetOnClose = false,
        };

        // Parameter values are never logged (EnableParameterLogging is not called).
        _dataSource = new NpgsqlDataSourceBuilder(connectionString.ConnectionString)
            .UseLoggerFactory(loggerFactory)
            // Commands are traced only inside a job run's span.
            .ConfigureTracing(tracing => tracing
                .EnablePhysicalOpenTracing(false)
                .ConfigureCommandFilter(_ => Activity.Current is not null))
            .Build();
    }

    /// <summary>Opens a connection to check the role; throws when it is not fit. The runner calls it at startup so a bad role stops the worker.</summary>
    public async Task VerifyRoleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        if (_roleVerified)
        {
            return connection;
        }

        try
        {
            if (!await DatabaseRoleGuard.IsDedicatedBypassRoleAsync(connection, cancellationToken))
            {
                throw new InvalidOperationException(
                    "The maintenance role must have BYPASSRLS, must be neither a superuser nor an owner of database objects, must hold no server privileges and must have no role memberships.");
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        _roleVerified = true;
        return connection;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
