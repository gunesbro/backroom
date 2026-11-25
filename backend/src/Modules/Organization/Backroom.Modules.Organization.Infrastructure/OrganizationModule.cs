using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Organization.Application.Invitations;
using Backroom.Modules.Organization.Application.Members;
using Backroom.Modules.Organization.Application.Provisioning;
using Backroom.Modules.Organization.Application.Queries;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure.Invitations;

namespace Backroom.Modules.Organization.Infrastructure;

public static class OrganizationModule
{
    /// <summary>The module's organization-scoped schema, for the migrator.</summary>
    public static IServiceCollection AddOrganizationDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<OrganizationDbContext>(OrganizationDbContext.Schema, organizationScoped: true);

    /// <summary>Organizations and members: provisioning, reads for members and the membership check. Needs Permissions' <c>IPermissionsProvisioning</c>.</summary>
    public static IServiceCollection AddOrganization(this IServiceCollection services)
    {
        services.AddOrganizationDatabase();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IOrganizationStore, OrganizationStore>();
        services.TryAddScoped<IOrganizationProvisioning, OrganizationProvisioning>();
        services.TryAddScoped<OrganizationReadStore>();
        services.TryAddScoped<IOrganizationReadStore>(provider => provider.GetRequiredService<OrganizationReadStore>());
        services.TryAddScoped<IOrganizationMembershipCheck>(provider => provider.GetRequiredService<OrganizationReadStore>());
        services.TryAddScoped<IMemberNames>(provider => provider.GetRequiredService<OrganizationReadStore>());
        services.TryAddScoped<OrganizationQueries>();
        return services;
    }

    /// <summary>
    /// Invitations: creating, listing, revoking and resending them, their registration with the control plane and their acceptance. Needs <see cref="AddOrganization"/>, Permissions' <c>IPermissionChecker</c>
    /// and the database session. The returned builder binds <see cref="InvitationKeyOptions"/>, which are checked at
    /// startup: without keys the service does not start.
    /// </summary>
    public static OptionsBuilder<InvitationKeyOptions> AddOrganizationInvitations(this IServiceCollection services)
    {
        services.AddOutbox();
        // The worker's relay publishes this module's outbox (only where the host runs AddOutboxRelay).
        services.Configure<OutboxRelayOptions>(options =>
        {
            if (!options.Schemas.Contains(OrganizationDbContext.Schema, StringComparer.Ordinal))
            {
                options.Schemas.Add(OrganizationDbContext.Schema);
            }
        });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InvitationKeyOptions>, InvitationKeyOptionsValidator>());
        services.TryAddSingleton<InvitationKeyRing>();
        services.TryAddSingleton<IInvitationCodeIssuer, InvitationCodeIssuer>();
        services.TryAddScoped<IInvitationStore, InvitationStore>();
        services.TryAddScoped<InvitationSendLimits>();
        services.TryAddScoped<InvitationCreation>();
        services.TryAddScoped<InvitationManagement>();
        services.TryAddScoped<InvitationQueries>();
        // Reads its options on first use; only the worker (AddOrganizationInvitationLinks) configures and validates them.
        services.TryAddSingleton<IInvitationLinkBuilder, InvitationLinkBuilder>();
        services.TryAddScoped<IInvitationLinks, InvitationLinks>();
        services.TryAddScoped<IInvitationRegistration, InvitationRegistration>();
        services.TryAddScoped<IMemberStore, MemberStore>();
        services.TryAddScoped<IInvitationAcceptance, InvitationAcceptance>();
        return services.AddOptions<InvitationKeyOptions>().ValidateOnStart();
    }

    /// <summary>
    /// The scheduled invitation jobs (expiry and email retention) as maintenance jobs (docs/adr/0010), for the worker
    /// role. They run in the host's <c>AddMaintenanceJobRunner</c> as the maintenance role, which reads every
    /// organization but only the columns this module's migration grants it.
    /// </summary>
    public static IServiceCollection AddOrganizationInvitationJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddMaintenanceJob<InvitationExpiryJob>();
        services.AddMaintenanceJob<InvitationRetentionJob>();
        return services;
    }

    /// <summary>
    /// Invitation links for the Email module (worker role, after <see cref="AddOrganizationInvitations"/>). The returned
    /// builder binds <see cref="InvitationLinkOptions"/>, which are checked at startup: without a valid web origin the
    /// worker does not start. Other roles build no links and need no origin.
    /// </summary>
    public static OptionsBuilder<InvitationLinkOptions> AddOrganizationInvitationLinks(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InvitationLinkOptions>, InvitationLinkOptionsValidator>());
        return services.AddOptions<InvitationLinkOptions>().ValidateOnStart();
    }
}
