using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Identity.Application;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Infrastructure;

public static class IdentityModule
{
    /// <summary>The module's schema, for the migrator (control-plane tables are not organization-scoped).</summary>
    public static IServiceCollection AddIdentityDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.Schema, organizationScoped: false);

    /// <summary>
    /// Session revocation and account status: the stores, the fan-out to the cells and what <c>FullSync</c> reports. Needs
    /// CellManagement (<c>ICellLinkOutbox</c>) and Directory (<c>IDirectoryQueries</c>) and the host's database session.
    /// </summary>
    public static IServiceCollection AddIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIdentityDatabase();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IRevokedSessionStore, RevokedSessionStore>();
        services.TryAddScoped<SessionRevocations>();
        services.TryAddScoped<IRevokedSessionQueries, RevokedSessionQueries>();
        services.TryAddSingleton<RevokedSessionCleanup>();

        // Account status (docs/contracts/phase-2.md, flow 9): the own status and its fan-out, and the statuses FullSync reports.
        services.TryAddScoped<IAccountStatusStore, AccountStatusStore>();
        services.TryAddScoped<AccountStatuses>();
        services.TryAddScoped<IAccountStatusQueries, AccountStatusQueries>();
        return services;
    }

    /// <summary>The scheduled cleanup of expired revocations, for the control plane host; needs <see cref="AddIdentity"/>.</summary>
    public static IServiceCollection AddIdentityJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<RevokedSessionCleanupWorker>();
        return services;
    }
}
