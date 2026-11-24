using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.ControlPlaneConnector.Infrastructure;
using Backroom.Modules.Email.Infrastructure;
using Backroom.Modules.Email.Infrastructure.Sending;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure;

namespace Backroom.Cell.Host;

/// <summary>The cell's modules. The migrate run registers only their databases.</summary>
internal static class CellModules
{
    public static IServiceCollection AddCellDatabases(this IServiceCollection services)
    {
        // Each cell module adds its DbContext (AddModuleDbContext, organization-scoped) here as it arrives.
        // The connector's link tables are migrated in every role, though only the worker uses them.
        return services
            .AddOrganizationDatabase()
            .AddMessagingDatabase()
            .AddPermissionsDatabase()
            .AddEmailDatabase()
            .AddControlPlaneConnectorDatabase();
    }

    public static IHostApplicationBuilder AddCellModules(this IHostApplicationBuilder builder, CellRole role)
    {
        builder.Services.AddCellDatabases();
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        // Live events: the placeholder publisher until the Valkey and SignalR gateway arrives (step 6.2).
        builder.Services.AddRealtime();
        builder.Services.AddMessaging();
        // Every role needs the invitation keys: the app issues codes, the worker will build the links.
        builder.Services.AddOrganizationInvitations().BindConfiguration(InvitationKeyOptions.SectionName);
        if (role == CellRole.Worker)
        {
            builder.AddControlPlaneConnector();
            // Only the worker sends email; the startup check stops it unless the target is Mailpit (docs/adr/0008).
            // Its invitation emails link to the web origin, checked at startup.
            builder.Services.AddOrganizationInvitationLinks().BindConfiguration(InvitationLinkOptions.SectionName);
            // Cross-organization jobs run as the maintenance role (docs/adr/0010): invitation expiry and retention.
            builder.Services.AddOrganizationInvitationJobs();
            builder.Services.AddMaintenanceJobRunner().BindConfiguration(MaintenanceJobOptions.SectionName);
            builder.Services.AddEmail().BindConfiguration(EmailOptions.SectionName);

            // The modules registered their event handlers and outbox schemas above. Consumers before the relay,
            // so their queues are declared before events are published.
            builder.Services.AddRabbitMq().BindConfiguration(RabbitMqOptions.SectionName);
            builder.Services.AddIntegrationEventConsumers().BindConfiguration(IntegrationEventConsumerOptions.SectionName);
            builder.Services.AddOutboxRelay().BindConfiguration(OutboxRelayOptions.SectionName);
        }

        return builder;
    }

    /// <summary>Applies the modules' migrations (role <c>migrate</c>), then returns.</summary>
    public static async Task MigrateAsync(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Services.AddCellDatabases();
        builder.Services.AddDatabaseMigrations(allowBootstrap: builder.Environment.IsDevelopment())
            .BindConfiguration(DatabaseMigrationOptions.SectionName);

        using var host = builder.Build();
        await host.StartAsync();
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(stopping);
        await host.StopAsync();
    }
}
