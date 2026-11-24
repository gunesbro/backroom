using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Events;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;
using InvitationCreated = Backroom.Modules.Organization.Contracts.InvitationCreated;
using InvitationRevoked = Backroom.Modules.Organization.Contracts.InvitationRevoked;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure;

public static class ControlPlaneConnectorModule
{
    /// <summary>The connector's schema (link inbox and outbox), for every role's migrations.</summary>
    public static IServiceCollection AddControlPlaneConnectorDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<ControlPlaneConnectorDbContext>(ControlPlaneConnectorDbContext.Schema, organizationScoped: false);

    /// <summary>
    /// The worker role's link to the control plane: registration and the link stream with its inbox and
    /// outbox. Needs the host's database session (<c>AddDatabaseSession</c>).
    /// </summary>
    public static IHostApplicationBuilder AddControlPlaneConnector(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.AddOptions<ControlPlaneConnectorOptions>()
            .BindConfiguration(ControlPlaneConnectorOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.CellId = configuration["Cell:Id"] ?? options.CellId;
                options.RelayConnectionString = configuration["OutboxRelay:ConnectionString"] ?? string.Empty;
                if (string.IsNullOrEmpty(options.Authority))
                {
                    options.Authority = configuration["Authentication:Authority"] ?? string.Empty;
                }
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.CellId), "Cell:Id is required.")
            .Validate(options => Uri.TryCreate(options.Authority, UriKind.Absolute, out _), "The Keycloak authority is required.")
            .Validate(options => options.ControlPlaneUrl is not null, "ControlPlaneConnector:ControlPlaneUrl is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.RelayConnectionString), "OutboxRelay:ConnectionString (the relay role) is required for link delivery.")
            .Validate(
                // The registration key and tokens travel in requests: only Development may use plain HTTP.
                options => options.ControlPlaneUrl?.Scheme == Uri.UriSchemeHttps || builder.Environment.IsDevelopment(),
                "ControlPlaneConnector:ControlPlaneUrl must use https outside Development.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CellKeyStore>();
        // A rejected registration or credentials make the worker unhealthy (Health/CellLinkStatus); a mere outage does not.
        services.TryAddSingleton<CellLinkStatus>();
        services.AddHealthChecks().AddCheck<CellLinkHealthCheck>(CellLinkHealthCheck.Name);
        services.TryAddSingleton<CellCredentials>();
        services.AddHttpClient(CellCredentials.HttpClientName);
        services.AddHttpClient(CellRegistrationWorker.HttpClientName);
        services.AddHostedService<CellRegistrationWorker>();

        // A long-lived HTTP/2 stream: no request timeout; keep-alive pings detect a dead connection.
        services.AddHttpClient(CellLinkWorker.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            });
        // The lease's own unpooled connection to the cell's database (Database:ConnectionString).
        services.AddKeyedSingleton(LinkLease.DataSourceKey, (provider, _) => LinkLease.CreateDataSource(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString is { Length: > 0 } connectionString
                ? connectionString
                : throw new InvalidOperationException("Database:ConnectionString is required for the link lease."),
            provider.GetService<ILoggerFactory>()));
        // Delivery runs as the relay role (OutboxRelay:ConnectionString): the application role may only
        // insert into the link tables. Only the worker role is given this connection.
        services.AddKeyedSingleton(UpstreamDelivery.DataSourceKey, (provider, _) =>
            new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(provider.GetRequiredService<IOptions<ControlPlaneConnectorOptions>>().Value.RelayConnectionString)
            {
                // One link per cell: a poll, an answer or two at a time.
                MaxPoolSize = 2,
                IncludeErrorDetail = false,
                ApplicationName = "backroom-link-delivery",
            }.ConnectionString)
                .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                .Build());
        services.TryAddSingleton<UpstreamDelivery>();
        services.TryAddScoped<UpstreamOutbox>();
        services.TryAddSingleton<DownstreamReceiver>();
        services.TryAddSingleton<UpstreamOutcomes>();

        // Integration events that become upstream messages; the host runs the consumers (AddIntegrationEventConsumers).
        services.AddIntegrationEventHandler<InvitationCreated, InvitationCreatedConsumer>(ControlPlaneConnectorDbContext.Schema);
        services.AddIntegrationEventHandler<InvitationRevoked, InvitationRevokedConsumer>(ControlPlaneConnectorDbContext.Schema);

        // Downstream handlers call the owning modules' contracts; the host adds those modules.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDownstreamHandler<OrganizationProvisioned>, OrganizationProvisionedHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDownstreamHandler<InvitationAccepted>, InvitationAcceptedHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDownstreamHandler<SessionRevoked>, SessionRevokedHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDownstreamHandler<AccountStatusChanged>, AccountStatusChangedHandler>());
        services.TryAddSingleton<FullSyncRunner>();
        services.TryAddSingleton<CellEpochStore>();
        // The worker's consumers and maintenance jobs wait for the confirmed epoch like the app role's writes.
        services.AddCellEpochFence();
        services.TryAddSingleton<CellLinkWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<CellLinkWorker>());
        return builder;
    }
}
