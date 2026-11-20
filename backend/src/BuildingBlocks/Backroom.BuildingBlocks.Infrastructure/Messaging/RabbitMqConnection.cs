using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// The process's single RabbitMQ connection, opened on first use. After network failures the client
/// recovers it automatically, including channels and topology, so it is never replaced while it
/// recovers. Channels are created per user (they are not thread-safe).
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { } created)
            {
                return created;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_options.ConnectionString),
                ClientProvidedName = _options.ClientName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };

            // If the first connection attempt fails, the next call tries again.
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }
}
