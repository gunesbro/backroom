using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Permissions.Infrastructure;

public static class PermissionsModule
{
    /// <summary>The module's organization-scoped schema, for the migrator.</summary>
    public static IServiceCollection AddPermissionsDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<PermissionsDbContext>(PermissionsDbContext.Schema, organizationScoped: true);

    /// <summary>Roles and permissions: provisioning a new organization's system roles, and permission checks.</summary>
    public static IServiceCollection AddPermissions(this IServiceCollection services)
    {
        services.AddPermissionsDatabase();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IPermissionsProvisioning, PermissionsProvisioning>();
        services.TryAddScoped<PermissionChecker>();
        services.TryAddScoped<IPermissionChecker>(provider => provider.GetRequiredService<PermissionChecker>());
        services.TryAddScoped<IOrganizationPermissionCheck>(provider => provider.GetRequiredService<PermissionChecker>());
        return services;
    }
}
