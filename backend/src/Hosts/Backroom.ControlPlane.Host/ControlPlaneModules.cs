using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Modules.CellManagement.Presentation;
using Backroom.Modules.Directory.Infrastructure;
using Backroom.Modules.Directory.Infrastructure.Invitations;
using Backroom.Modules.Directory.Presentation;
using Backroom.Modules.Identity.Infrastructure;
using Backroom.Modules.Identity.Presentation;

namespace Backroom.ControlPlane.Host;

/// <summary>The control plane's modules. The migrate run registers only their databases.</summary>
internal static class ControlPlaneModules
{
    public const string MigrateCommand = "migrate";

    public static IServiceCollection AddControlPlaneDatabases(this IServiceCollection services) =>
        services.AddCellManagementDatabase().AddDirectoryDatabase().AddIdentityDatabase();

    public static IHostApplicationBuilder AddControlPlaneModules(this IHostApplicationBuilder builder)
    {
        builder.AddCellManagement();
        builder.Services.AddCellManagementEndpoints();
        builder.Services.AddDirectory().BindConfiguration(InvitationEmailHmacOptions.SectionName);
        builder.Services.AddDirectoryJobs();
        builder.Services.AddDirectoryEndpoints();

        // Keycloak's back-channel logout: revoked sessions go to the cells; expired revocations are deleted.
        builder.Services.AddIdentity();
        builder.Services.AddIdentityJobs();
        builder.Services.AddIdentityEndpoints();
        return builder;
    }

    public static IEndpointRouteBuilder MapControlPlaneModules(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapCellManagementEndpoints().MapDirectoryEndpoints().MapIdentityEndpoints();

    /// <summary>Applies the modules' migrations (command <c>migrate</c>), then returns.</summary>
    public static async Task MigrateAsync(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Services.AddControlPlaneDatabases();
        builder.Services.AddDatabaseMigrations(allowBootstrap: builder.Environment.IsDevelopment())
            .BindConfiguration(DatabaseMigrationOptions.SectionName);

        using var host = builder.Build();
        await host.StartAsync();
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(stopping);
        await host.StopAsync();
    }
}
