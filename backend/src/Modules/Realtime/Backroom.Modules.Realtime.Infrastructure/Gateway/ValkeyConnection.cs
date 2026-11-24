using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// The process's one Valkey connection, shared by the SignalR backplane and the member-removal channel. Connecting does
/// not fail when Valkey is down (it keeps trying), so the cell starts without it and live events resume when it returns.
/// Every pub/sub channel carries the cell's prefix, because development cells share one server.
/// </summary>
internal sealed class ValkeyConnection(IOptions<ValkeyOptions> valkey, IOptions<RealtimeOptions> realtime) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private Task<IConnectionMultiplexer>? _connection;

    public Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_connection is null or { IsFaulted: true })
            {
                _connection = ConnectAsync();
            }

            return _connection.WaitAsync(cancellationToken);
        }
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
        var timeout = realtime.Value.PublishTimeout;
        var options = ConfigurationOptions.Parse(valkey.Value.ConnectionString);
        options.AbortOnConnectFail = false;
        options.AllowAdmin = false;
        options.ClientName = "backroom-cell-" + realtime.Value.CellId;
        options.ChannelPrefix = RedisChannel.Literal("backroom:cell:" + realtime.Value.CellId + ":");

        // A command waits at most this long, so a down server fails a publish instead of holding it.
        options.AsyncTimeout = (int)timeout.TotalMilliseconds;
        options.SyncTimeout = (int)timeout.TotalMilliseconds;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
