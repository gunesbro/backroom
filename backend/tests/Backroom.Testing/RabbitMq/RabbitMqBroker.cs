using Testcontainers.RabbitMq;
using Xunit;

namespace Backroom.Testing.RabbitMq;

/// <summary>
/// A RabbitMQ broker in a container for integration tests. Share one instance per test assembly
/// (<c>[assembly: AssemblyFixture(typeof(RabbitMqBroker))]</c>); tests use their own queues.
/// </summary>
public sealed class RabbitMqBroker : IAsyncLifetime
{
    /// <summary>The same major and minor version as the AppHost's RabbitMQ container.</summary>
    public const string Image = "rabbitmq:4.3";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(Image).Build();

    /// <summary>An <c>amqp://</c> URI with the container's generated credentials.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
