using Microsoft.EntityFrameworkCore;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// A module's schema and DbContext, registered by <see cref="PersistenceServiceCollectionExtensions.AddModuleDbContext{TContext}"/>
/// so the migrator can apply the module's migrations.
/// </summary>
/// <param name="Schema">The module's schema.</param>
/// <param name="OrganizationScoped">
/// Whether every table holds organization data and must pass <see cref="RowLevelSecurityAudit"/>
/// (all cell modules; control plane tables are cross-organization and are not).
/// </param>
/// <param name="CreateForMigration">Creates the context on the migrator's connection string.</param>
public sealed record ModuleDatabase(string Schema, bool OrganizationScoped, Func<IServiceProvider, string, DbContext> CreateForMigration);
