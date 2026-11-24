using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>Applies the migrations of every module the tests use, once for the assembly's database.</summary>
internal static class RealtimeSchema
{
    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static bool s_migrated;

    public static async Task MigrateOnceAsync(PostgresDatabase database, CancellationToken cancellationToken)
    {
        await s_lock.WaitAsync(cancellationToken);
        try
        {
            if (s_migrated)
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPermissionsDatabase();
            services.AddOrganizationDatabase();
            services.AddMessagingDatabase();
            services.AddRealtimeDatabase();
            services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = database.MigratorConnectionString);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
            s_migrated = true;
        }
        finally
        {
            s_lock.Release();
        }
    }
}
