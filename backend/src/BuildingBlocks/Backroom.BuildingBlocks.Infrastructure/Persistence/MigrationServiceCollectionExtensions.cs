using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

public static class MigrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="DatabaseMigrator"/> for the modules registered with
    /// <c>AddModuleDbContext</c>. Role bootstrap is accepted only when <paramref name="allowBootstrap"/>
    /// is set (the Development environment); elsewhere configuring it stops the run. The returned
    /// builder binds <see cref="DatabaseMigrationOptions"/>; the settings are validated at startup.
    /// </summary>
    public static OptionsBuilder<DatabaseMigrationOptions> AddDatabaseMigrations(this IServiceCollection services, bool allowBootstrap)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DatabaseMigrator>();
        return services.AddOptions<DatabaseMigrationOptions>()
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.MigratorConnectionString),
                "DatabaseMigration:MigratorConnectionString is required.")
            .Validate(
                options => string.IsNullOrEmpty(options.BootstrapConnectionString) || allowBootstrap,
                "DatabaseMigration:BootstrapConnectionString is accepted only in Development; deployments provision the roles.")
            .Validate(
                options => string.IsNullOrEmpty(options.BootstrapConnectionString)
                    || (!string.IsNullOrWhiteSpace(options.MigratorPassword)
                        && !string.IsNullOrWhiteSpace(options.ApplicationPassword)
                        && !string.IsNullOrWhiteSpace(options.RelayPassword)
                        && !string.IsNullOrWhiteSpace(options.MaintenancePassword)),
                "The role bootstrap needs the migrator, application, relay and maintenance passwords.")
            .ValidateOnStart();
    }
}
