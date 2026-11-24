using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.CellManagement.Application.Provisioning;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.ControlPlaneConnector.Infrastructure;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;
using Npgsql;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>A cell's connector services, talking to the control plane on its test server; its key directory is deleted on dispose.</summary>
internal sealed class TestCell : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TestCell(ServiceProvider provider, string cellId, string keyDirectory)
    {
        _provider = provider;
        CellId = cellId;
        KeyDirectory = keyDirectory;
    }

    public string CellId { get; }

    public string KeyDirectory { get; }

    /// <summary>What the connector applied through Realtime's contract; needs the database given to <see cref="Create"/>.</summary>
    public RecordingSessionRevocation Revocations => _provider.GetRequiredService<RecordingSessionRevocation>();

    public CellRegistrationWorker Worker => _provider.GetRequiredService<CellRegistrationWorker>();

    /// <summary>Whether the cell is rejected; what its health check reports.</summary>
    public CellLinkStatus Status => _provider.GetRequiredService<CellLinkStatus>();

    public CellCredentials Credentials => _provider.GetRequiredService<CellCredentials>();

    /// <summary>The link worker; needs the database given to <see cref="Create"/>.</summary>
    public CellLinkWorker Link => _provider.GetRequiredService<CellLinkWorker>();

    /// <summary>The cell's services (database session, link outbox); needs the database given to <see cref="Create"/>.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>A new pending cell and its registration key.</summary>
    public static async Task<(string CellId, string Key)> ProvisionAsync(WebApplication controlPlane, CancellationToken cancellationToken)
    {
        var cellId = "t" + Guid.CreateVersion7().ToString("N")[^10..];
        var result = await controlPlane.Services.GetRequiredService<CellProvisioningService>().ProvisionAsync(
            new NewCell(cellId, "Test cell", "shared", "eu-test", new Uri("https://cell.example.test")), cancellationToken);
        var provisioned = result.GetValueOrThrow();
        return (provisioned.Id, provisioned.RegistrationKey);
    }

    /// <summary>A new cell, provisioned and registered.</summary>
    public static async Task<TestCell> RegisterAsync(
        WebApplication controlPlane,
        KeycloakRealm keycloak,
        CancellationToken cancellationToken,
        PostgresDatabase? database = null,
        TimeProvider? linkTime = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var (cellId, key) = await ProvisionAsync(controlPlane, cancellationToken);
        var cell = Create(controlPlane, keycloak, cellId, key, database, linkTime, configureServices);
        if (!await cell.Worker.RegisterAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Test cell {cellId} did not register.");
        }

        return cell;
    }

    public static TestCell Create(
        WebApplication controlPlane,
        KeycloakRealm keycloak,
        string cellId,
        string registrationKey,
        PostgresDatabase? database = null,
        TimeProvider? linkTime = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var keyDirectory = Path.Combine(Path.GetTempPath(), "backroom-cell-tests", Guid.CreateVersion7().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IOptions<ControlPlaneConnectorOptions>>(Options.Create(new ControlPlaneConnectorOptions
        {
            CellId = cellId,
            Authority = keycloak.Url,
            ControlPlaneUrl = new Uri("http://localhost/"),
            RegistrationKey = registrationKey,
            KeyDirectory = keyDirectory,
            LinkLeaseRetryInterval = TimeSpan.FromMilliseconds(200),
        }));
        services.AddSingleton<CellKeyStore>();
        services.AddSingleton<CellLinkStatus>();
        services.AddSingleton<CellCredentials>();
        services.AddSingleton<CellRegistrationWorker>();
        services.AddHttpClient(CellCredentials.HttpClientName);
        services.AddHttpClient(CellRegistrationWorker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => controlPlane.GetTestServer().CreateHandler());
        services.AddSingleton(NullLoggerFactory.Instance);
        if (database is not null)
        {
            var databaseConnectionString = database.ApplicationConnectionString();
            services.AddKeyedSingleton(LinkLease.DataSourceKey, (_, _) => LinkLease.CreateDataSource(databaseConnectionString));
            services.AddDatabaseSession().Configure(options => options.ConnectionString = databaseConnectionString);
            services.AddKeyedSingleton(UpstreamDelivery.DataSourceKey, (_, _) => NpgsqlDataSource.Create(database.RelayConnectionString));
            services.AddSingleton<UpstreamDelivery>();
            services.AddScoped<UpstreamOutbox>();
            services.AddSingleton<DownstreamReceiver>();
            services.AddSingleton<UpstreamOutcomes>();
            services.AddSingleton<RecordingSessionRevocation>();
            services.AddSingleton<Backroom.Modules.Realtime.Contracts.ISessionRevocation>(provider => provider.GetRequiredService<RecordingSessionRevocation>());
            services.AddSingleton<FullSyncRunner>();
            services.AddScoped<IDownstreamHandler<Backroom.ControlPlane.Contracts.V1.SessionRevoked>, SessionRevokedHandler>();
            services.AddHttpClient(CellLinkWorker.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => controlPlane.GetTestServer().CreateHandler());
            // linkTime: a separate clock for the link worker only (the token cache keeps real time).
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<CellLinkWorker>(provider, linkTime ?? TimeProvider.System));
        }

        configureServices?.Invoke(services);
        return new TestCell(services.BuildServiceProvider(), cellId, keyDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (System.IO.Directory.Exists(KeyDirectory))
        {
            System.IO.Directory.Delete(KeyDirectory, recursive: true);
        }
    }
}
