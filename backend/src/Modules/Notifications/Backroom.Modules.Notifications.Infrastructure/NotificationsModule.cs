using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Notifications.Application;
using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Modules.Notifications.Application.Delivery;
using Backroom.Modules.Notifications.Application.Settings;
using Backroom.BuildingBlocks.Infrastructure.Messaging;

namespace Backroom.Modules.Notifications.Infrastructure;

public static class NotificationsModule
{
    /// <summary>The feature flag that turns notifications on (docs/contracts/phase-2.md, "Decisions", 27).</summary>
    public const string Flag = "Features:Notifications";

    /// <summary>The module's organization-scoped schema, for the migrator.</summary>
    public static IServiceCollection AddNotificationsDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.Schema, organizationScoped: true);

    /// <summary>
    /// The bell, the settings and the decision order. Needs the database session, Messaging's <c>IConversationReadAccess</c> and
    /// <c>IMessagePreviews</c> (<c>AddMessaging</c>) and Presence's <c>IPresenceReader</c> (<c>AddPresence</c>).
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNotificationsDatabase();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdGenerator>(provider => new UuidV7IdGenerator(provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<INotificationStore, NotificationStore>();
        services.TryAddScoped<BellService>();
        services.TryAddScoped<SettingsService>();
        return services;
    }

    /// <summary>Worker role: <c>messaging.activity-added.v1</c> → bell entries and live events. Needs <see cref="AddNotifications"/> and <c>AddRealtime</c>.</summary>
    public static IServiceCollection AddNotificationConsumers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddIntegrationEventHandler<ActivityAdded, ActivityAddedHandler>(NotificationsDbContext.Schema);
    }
}
