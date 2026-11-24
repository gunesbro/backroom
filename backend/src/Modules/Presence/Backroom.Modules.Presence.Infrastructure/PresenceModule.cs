using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

public static class PresenceModule
{
    /// <summary>The feature flag (the control plane's status endpoints use the same name).</summary>
    public const string Flag = "Features:Presence";

    /// <summary>
    /// Presence in every role: the Valkey store, the account status copy the connector applies, the reader for the organization
    /// endpoints and the membership hook. Needs <c>Valkey:ConnectionString</c> and <c>Cell:Id</c> (checked at startup) and Realtime's
    /// <c>ILiveEventPublisher</c>. The app role also needs <see cref="AddPresenceTracking"/>.
    /// </summary>
    public static IServiceCollection AddPresence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<PresenceValkeyOptions>()
            .BindConfiguration(PresenceValkeyOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Valkey:ConnectionString is required.")
            .ValidateOnStart();
        services.AddOptions<PresenceOptions>()
            .BindConfiguration(PresenceOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.CellId = configuration["Cell:Id"] ?? options.CellId;
                options.Enabled = configuration.GetValue<bool>(Flag);
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.CellId) && !options.CellId.Contains(':', StringComparison.Ordinal),
                "Cell:Id is required and must not contain ':'.")
            .Validate(
                options => options.IdleAfter >= TimeSpan.FromMinutes(1) && options.IdleAfter <= TimeSpan.FromHours(2)
                    && options.OfflineAfter >= TimeSpan.FromSeconds(10) && options.OfflineAfter <= TimeSpan.FromMinutes(10)
                    && options.RefreshInterval > TimeSpan.Zero && options.RefreshInterval * 2 <= options.OfflineAfter
                    && options.EventInterval >= TimeSpan.FromSeconds(1) && options.EventInterval <= TimeSpan.FromMinutes(1)
                    && options.SweepInterval > TimeSpan.Zero && options.SweepInterval <= options.EventInterval
                    && options.SweepLease > options.SweepInterval && options.SweepLease <= TimeSpan.FromMinutes(5)
                    && options.WatchLifetime >= TimeSpan.FromMinutes(1) && options.WatchLifetime <= TimeSpan.FromHours(1)
                    && options.StatusRetention >= TimeSpan.FromHours(1) && options.StatusRetention <= TimeSpan.FromDays(30)
                    && options.CommandTimeout > TimeSpan.Zero && options.CommandTimeout <= TimeSpan.FromSeconds(30)
                    && options.SweepBatchSize is > 0 and <= 10_000
                    && options.MaxOrganizationsPerPerson is > 0 and <= 1000
                    && options.MaxConnections is > 0 and <= 1_000_000,
                "The Presence timings or limits are out of range (the refresh must fit twice into the offline time).")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PresenceValkey>();
        services.TryAddSingleton<PresenceStore>();
        services.TryAddSingleton<PresencePublisher>();
        services.TryAddSingleton<IAccountStatusSink, AccountStatusSink>();
        services.Replace(ServiceDescriptor.Singleton<IPresenceMemberships, PresenceMemberships>());
        services.TryAddScoped<IPresenceReader, PresenceReader>();
        return services;
    }

    /// <summary>App role, after <see cref="AddPresence"/>: the hub's tracker and the node's sweeper.</summary>
    public static IServiceCollection AddPresenceTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PresenceTracker>();
        services.Replace(ServiceDescriptor.Singleton<IPresenceTracker>(provider => provider.GetRequiredService<PresenceTracker>()));
        services.AddHostedService<PresenceSweeper>();
        return services;
    }
}
