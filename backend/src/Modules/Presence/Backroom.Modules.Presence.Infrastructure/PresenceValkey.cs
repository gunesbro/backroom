using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// Presence's Valkey connection (the cell's server, <c>Valkey:ConnectionString</c>). Connecting does not fail while Valkey is down
/// (it keeps trying), and a command on a connection that is down fails at once, so presence never holds up a hub call.
/// </summary>
internal sealed class PresenceValkey(IOptions<PresenceValkeyOptions> valkey, IOptions<PresenceOptions> presence) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private Task<IConnectionMultiplexer>? _connection;

    public async Task<IDatabase> DatabaseAsync(CancellationToken cancellationToken)
    {
        Task<IConnectionMultiplexer> connection;
        lock (_lock)
        {
            if (_connection is null or { IsFaulted: true })
            {
                _connection = ConnectAsync();
            }

            connection = _connection;
        }

        return (await connection.WaitAsync(cancellationToken)).GetDatabase();
    }

    public async ValueTask DisposeAsync()
    {
        Task<IConnectionMultiplexer>? connection;
        lock (_lock)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is { IsCompletedSuccessfully: true })
        {
            await connection.Result.DisposeAsync();
        }
    }

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        var timeout = (int)presence.Value.CommandTimeout.TotalMilliseconds;
        var options = ConfigurationOptions.Parse(valkey.Value.ConnectionString);
        options.AbortOnConnectFail = false;
        options.AllowAdmin = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ClientName = "backroom-presence-" + presence.Value.CellId;
        options.AsyncTimeout = timeout;
        options.SyncTimeout = timeout;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
