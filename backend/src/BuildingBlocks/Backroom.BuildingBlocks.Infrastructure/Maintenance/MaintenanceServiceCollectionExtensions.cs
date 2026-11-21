using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

public static class MaintenanceServiceCollectionExtensions
{
    /// <summary>
    /// Runs the registered maintenance jobs as a hosted service (worker role; docs/adr/0010). The returned
    /// builder binds <see cref="MaintenanceJobOptions"/>. The maintenance connection pool is owned by the
    /// runner and is deliberately not registered in dependency injection.
    /// </summary>
    public static OptionsBuilder<MaintenanceJobOptions> AddMaintenanceJobRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MaintenanceJobOptions>, MaintenanceJobOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MaintenanceJobRunner>());
        return services.AddOptions<MaintenanceJobOptions>().ValidateOnStart();
    }

    /// <summary>
    /// Registers <typeparamref name="TJob"/> with the runner (requires <see cref="AddMaintenanceJobRunner"/>).
    /// The job's module grants the maintenance role the columns it needs in a migration (<see cref="MaintenanceGrants"/>).
    /// A job that also implements <see cref="IMaintenanceBacklog"/> reports its backlog.
    /// </summary>
    public static IServiceCollection AddMaintenanceJob<TJob>(this IServiceCollection services)
        where TJob : class, IMaintenanceJob
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMaintenanceJob, TJob>();
        return services;
    }
}
