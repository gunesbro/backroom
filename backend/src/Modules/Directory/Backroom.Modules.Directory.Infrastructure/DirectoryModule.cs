using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Application.Invitations;
using Backroom.Modules.Directory.Application.Organizations;
using Backroom.Modules.Directory.Infrastructure.Invitations;

namespace Backroom.Modules.Directory.Infrastructure;

public static class DirectoryModule
{
    /// <summary>The module's schema, for the migrator (control-plane tables are not organization-scoped).</summary>
    public static IServiceCollection AddDirectoryDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<DirectoryDbContext>(DirectoryDbContext.Schema, organizationScoped: false);

    /// <summary>Organizations, memberships and invitations. Needs CellManagement's <c>ICellCatalog</c>. The returned builder binds <see cref="InvitationEmailHmacOptions"/>.</summary>
    public static OptionsBuilder<InvitationEmailHmacOptions> AddDirectory(this IServiceCollection services)
    {
        services.AddDirectoryDatabase();
        services.TryAddSingleton<IOrganizationDirectory, OrganizationDirectory>();
        services.TryAddScoped<OrganizationQueries>();
        services.TryAddScoped<IOrganizationStore, OrganizationStore>();
        services.TryAddScoped<OrganizationCreation>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICellMessageOutcomeHandler<OrganizationProvisioned>, OrganizationProvisioningOutcome>());
        services.TryAddSingleton<ICellOrganizationScope, CellOrganizationScope>();
        services.TryAddSingleton<ICellOrganizationCounts, CellOrganizationCounts>();

        // Invitations registered and revoked by cells. The email HMAC key is checked at startup: without it the control plane does not start.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InvitationEmailHmacOptions>, InvitationEmailHmacOptionsValidator>());
        services.TryAddSingleton<IInvitationEmailHmac, InvitationEmailHmac>();
        services.TryAddScoped<IInvitationRegistry, InvitationRegistry>();
        services.TryAddScoped<InvitationLinkMessages>();
        services.TryAddScoped<InvitationResolution>();
        services.TryAddScoped<InvitationAcceptance>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICellMessageOutcomeHandler<InvitationAccepted>, InvitationAcceptanceOutcome>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICellMessageHandler<InvitationRegistered>, InvitationLinkMessages>(provider => provider.GetRequiredService<InvitationLinkMessages>()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICellMessageHandler<InvitationRevoked>, InvitationLinkMessages>(provider => provider.GetRequiredService<InvitationLinkMessages>()));
        services.TryAddSingleton<InvitationJobs>();
        return services.AddOptions<InvitationEmailHmacOptions>().ValidateOnStart();
    }

    /// <summary>The scheduled invitation jobs (expiry and retention), for the control plane host; needs <see cref="AddDirectory"/>.</summary>
    public static IServiceCollection AddDirectoryJobs(this IServiceCollection services)
    {
        services.AddHostedService<InvitationJobsWorker>();
        return services;
    }
}
