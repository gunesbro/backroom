using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Email.Infrastructure;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>Applies the Email migrations once for the assembly's database, whichever test class comes first.</summary>
internal static class EmailSchema
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
            services.AddEmailDatabase();
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
