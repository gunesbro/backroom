using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// The right to hold the cell's link: a PostgreSQL advisory lock per cell, so only one worker of the
/// cell keeps a stream (docs/contracts/phase-1.md, "Stream and identity"). The lock lives on an
/// unpooled connection of its own (<see cref="DataSourceKey"/>): closing it always ends the database
/// session and with it the lock, and it takes no connection from the application pool.
/// </summary>
internal sealed class LinkLease : IAsyncDisposable
{
    /// <summary>The keyed service name of the lease's unpooled data source.</summary>
    public const string DataSourceKey = "control-plane-link-lease";

    /// <summary>The lock class: "HLNK" in ASCII; the second key is derived from the cell id.</summary>
    private const int LockClass = 0x484C4E4B;

    private readonly NpgsqlConnection _connection;

    private LinkLease(NpgsqlConnection connection) => _connection = connection;

    /// <summary>The cell's lease, or <see langword="null"/> if another worker holds it.</summary>
    public static async Task<LinkLease?> TryAcquireAsync(NpgsqlDataSource dataSource, string cellId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var cellKey = CellKey(cellId);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1, $2)", connection);
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = LockClass });
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = cellKey });
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return new LinkLease(connection);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    /// <summary>Throws if the lock's connection is gone (the lock went with it).</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT 1", _connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>Closing the unpooled connection ends the database session, which releases the lock.</summary>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>A stable 32-bit key per cell id.</summary>
    internal static int CellKey(string cellId) =>
        BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(cellId)));

    /// <summary>The lease's data source: the cell's database without pooling.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            IncludeErrorDetail = false,
            ApplicationName = "backroom-link-lease",
        }.ConnectionString);
        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        return builder.Build();
    }
}
