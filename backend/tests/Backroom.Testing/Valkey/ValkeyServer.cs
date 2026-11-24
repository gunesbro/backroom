using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Backroom.Testing.Valkey;

/// <summary>
/// A Valkey server in a container for integration tests (the SignalR backplane and pub/sub). Share one instance per
/// test assembly (<c>[assembly: AssemblyFixture(typeof(ValkeyServer))]</c>); tests separate themselves by cell id, which
/// prefixes every channel.
/// </summary>
public sealed class ValkeyServer : IAsyncLifetime
{
    /// <summary>The same version as the AppHost's Valkey (checked by <c>ValkeyVersionTests</c>).</summary>
    public const string Version = "9.1.3";

    private const string Image = "valkey/valkey:" + Version;
    private const int ContainerPort = 6379;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(ContainerPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("valkey-cli", "ping"))
        .Build();

    /// <summary>A StackExchange.Redis connection string for the container.</summary>
    public string ConnectionString => $"{_container.Hostname}:{_container.GetMappedPublicPort(ContainerPort)}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
