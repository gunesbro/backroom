using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Name of the EF Core migrations history table in each module's schema.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Registers the organization context, the unit of work and the application role's connection pool.
    /// The returned builder binds <see cref="DatabaseOptions"/>; the settings are validated at startup.
    /// </summary>
    public static OptionsBuilder<DatabaseOptions> AddDatabaseSession(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<OrganizationContext>();
        services.TryAddScoped<IOrganizationContext>(provider => provider.GetRequiredService<OrganizationContext>());
        services.TryAddSingleton<DatabaseRoleGuard>();
        services.TryAddScoped<DatabaseSession>();
        services.TryAddScoped<IUnitOfWork>(provider => provider.GetRequiredService<DatabaseSession>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString)
            {
                MaxPoolSize = options.MaxPoolSize,
                // Server error details can contain row values, that is personal data.
                IncludeErrorDetail = false,
                // Session state is reset when a connection returns to the pool, whatever the deployment sets.
                NoResetOnClose = false,
            };

            // Parameter values are never logged (EnableParameterLogging is not called).
            return new NpgsqlDataSourceBuilder(connectionString.ConnectionString)
                .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                .Build();
        });

        return services.AddOptions<DatabaseOptions>().ValidateOnStart();
    }

    /// <summary>
    /// Registers a module's DbContext on the unit of work's connection, with its migrations history in
    /// the module's <paramref name="schema"/>. The context may be created at any time, but it sends
    /// commands only inside the active unit of work, whose transaction they join; SaveChanges does not
    /// start transactions of its own. Queries do not track entities by default; use <c>AsTracking()</c>
    /// to load entities for changes. The module's migrations are applied by the migrator
    /// (<see cref="MigrationServiceCollectionExtensions.AddDatabaseMigrations"/>); with
    /// <paramref name="organizationScoped"/> every table must also pass <see cref="RowLevelSecurityAudit"/>.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema, bool organizationScoped = true)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        OrganizationRowLevelSecurity.ValidateIdentifier(schema, nameof(schema));

        // Built once; each scope adds only its own guard. EF Core caches its internal services by these
        // options (interceptors are not part of that cache key).
        var baseOptions = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        var factory = ActivatorUtilities.CreateFactory<TContext>([typeof(DbContextOptions<TContext>)]);

        services.TryAddScoped(provider =>
        {
            var session = provider.GetRequiredService<DatabaseSession>();
            var options = new DbContextOptionsBuilder<TContext>(baseOptions)
                .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                .AddInterceptors(new UnitOfWorkCommandGuard(session))
                .Options;

            var context = factory(provider, [options]);
            context.Database.SetDbConnection(session.UnderlyingConnection, contextOwnsConnection: false);
            // Npgsql runs every command in the connection's open transaction, which the unit of work owns.
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            return context;
        });

        services.AddSingleton(new ModuleDatabase(
            schema,
            organizationScoped,
            (provider, migratorConnectionString) => factory(provider, [
                new DbContextOptionsBuilder<TContext>()
                    .UseNpgsql(migratorConnectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema))
                    .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                    .Options,
            ])));

        return services;
    }
}
