using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.Modules.CellManagement.Application.Provisioning;
using Backroom.Modules.CellManagement.Infrastructure.Link;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.CellManagement.Infrastructure.Keycloak;

namespace Backroom.Modules.CellManagement.Infrastructure;

public static class CellManagementModule
{
    /// <summary>The module's schema, for the migrator (control-plane tables are not organization-scoped).</summary>
    public static IServiceCollection AddCellManagementDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<CellManagementDbContext>(CellManagementDbContext.Schema, organizationScoped: false);

    /// <summary>
    /// The registry, cell registration and its Keycloak administration. The development seeder of the
    /// AppHost's cells is added only in Development.
    /// </summary>
    public static IHostApplicationBuilder AddCellManagement(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddCellManagementDatabase();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CellRegistry>();
        services.TryAddSingleton<ICellRegistry>(provider => provider.GetRequiredService<CellRegistry>());
        services.TryAddSingleton<KeycloakCellIdentityProvider>();
        services.TryAddSingleton<ICellIdentityProvider>(provider => provider.GetRequiredService<KeycloakCellIdentityProvider>());
        services.TryAddSingleton<CellRegistrationService>();
        services.TryAddSingleton<CellProvisioningService>();
        // The epoch increment ends the cell's stream in this instance (docs/adr/0011, "Cell epoch").
        services.TryAddSingleton<CellLinkSessions>();
        services.TryAddSingleton<Application.Epoch.CellEpochService>();
        services.TryAddSingleton<Application.Listing.CellListService>();
        services.TryAddSingleton<Application.Listing.OpsAudit>();
        // The list needs Directory's organization counts: without them the control plane does not start.
        services.AddHostedService<RequireCellListDependencies>();
        services.TryAddSingleton<ICellCatalog, CellCatalog>();
        services.TryAddScoped<ICellLinkOutbox, CellLinkOutbox>();
        services.TryAddScoped<IOutboundMessages, OutboundMessages>();
        services.TryAddSingleton<DownstreamDelivery>();
        services.TryAddScoped<IInboundMessages, InboundMessages>();
        services.TryAddSingleton<UpstreamReceiver>();

        services.AddHttpClient(KeycloakCellIdentityProvider.HttpClientName);
        services.AddOptions<KeycloakAdminOptions>()
            .BindConfiguration(KeycloakAdminOptions.SectionName)
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((options, configuration) =>
            {
                if (string.IsNullOrEmpty(options.Authority))
                {
                    options.Authority = configuration["Authentication:Authority"] ?? string.Empty;
                }
            })
            .Validate(options => Uri.TryCreate(options.Authority, UriKind.Absolute, out _), "The Keycloak authority is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.AdminClientSecret), "CellManagement:Keycloak:AdminClientSecret is required.")
            .ValidateOnStart();

        var environment = builder.Environment;
        services.AddOptions<SharedCellOptions>()
            .BindConfiguration(SharedCellOptions.SectionName)
            .Validate(options => options.Max >= 1, "CellManagement:SharedCells:Max must be at least 1.")
            .Validate(
                options => environment.IsDevelopment() || options.Max <= SharedCellOptions.ContractMax,
                "CellManagement:SharedCells:Max is at most 20 outside Development until sign-in uses pushed authorization requests.")
            .ValidateOnStart();
        // The Application project has no options dependency, so its services take the validated value.
        services.TryAddSingleton(provider => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SharedCellOptions>>().Value);

        if (builder.Environment.IsDevelopment())
        {
            services.AddOptions<DevelopmentCellOptions>().BindConfiguration(DevelopmentCellOptions.SectionName);
            services.AddHostedService<DevelopmentCellSeeder>();
        }
        else if (builder.Configuration.GetSection(DevelopmentCellOptions.SectionName + ":DevelopmentCells").GetChildren().Any())
        {
            // Refused, not ignored: a development registration key must never reach another environment.
            throw new InvalidOperationException("CellManagement:DevelopmentCells is accepted only in Development.");
        }

        return builder;
    }
}
