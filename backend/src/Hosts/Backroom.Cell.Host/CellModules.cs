using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.ControlPlaneConnector.Infrastructure;
using Backroom.Modules.Email.Infrastructure;
using Backroom.Modules.Email.Infrastructure.Sending;
using Backroom.Modules.Files.Infrastructure;
using Backroom.Modules.Files.Infrastructure.Storage;
using Backroom.Modules.Files.Presentation;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Modules.Notifications.Infrastructure;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.Presence.Infrastructure;
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
            .AddFilesDatabase()
            .AddNotificationsDatabase()
            .AddPermissionsDatabase()
            .AddEmailDatabase()
            .AddRealtimeDatabase()
            .AddControlPlaneConnectorDatabase();
    }

    public static IHostApplicationBuilder AddCellModules(this IHostApplicationBuilder builder, CellRole role)
    {
        builder.Services.AddCellDatabases();
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        // Live events: the publisher, the Valkey connection and the SignalR backplane, in every role (Messaging publishes
        // from the app role; the worker consumes member-removed).
        builder.Services.AddRealtime();
        builder.Services.AddMessaging();
        // Presence in Valkey (no schema): the status copy the worker applies, the reader of the app's endpoint.
        builder.Services.AddPresence();
        // Notifications (Phase 2 step 29) are behind a flag until the gate: the bell and the settings in the app role, the
        // decision on messaging.activity-added.v1 in the worker.
        var notifications = builder.Configuration.GetValue<bool>(NotificationsModule.Flag);
        if (notifications)
        {
            builder.Services.AddNotifications();
        }

        if (role == CellRole.App)
        {
            // Files (Phase 2 step 21) are behind a flag until the gate; only the app role signs uploads and downloads.
            // The storage keys are checked at startup, so a cell with the flag on and no storage does not start.
            if (builder.Configuration.GetValue<bool>(FilesEndpoints.FilesFlag))
            {
                builder.Services.AddFiles().BindConfiguration(S3StorageOptions.SectionName);
            }

            // The hub's checks and the listener that removes connections from groups (the hub itself is mapped in Program).
            builder.Services.AddRealtimeGateway();
            // Online and idle from the hub's connections, and the node's presence sweeper.
            builder.Services.AddPresenceTracking();
        }

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
            // Files: the malware scan of completed uploads (Phase 2 step 23), behind the same flag; the scanner and storage
            // settings are checked at startup, so a worker with the flag on and no scanner does not start.
            if (builder.Configuration.GetValue<bool>(FilesEndpoints.FilesFlag))
            {
                builder.Services.AddFileScanning().BindConfiguration(S3StorageOptions.SectionName);
            }

            builder.Services.AddEmail().BindConfiguration(EmailOptions.SectionName);
            // member-removed → every app node removes the account's connections from the organization's groups.
            builder.Services.AddRealtimeMemberRemoval();
            if (notifications)
            {
                builder.Services.AddNotificationConsumers();
            }

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
