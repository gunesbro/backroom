using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.ControlPlane.Host;
using Backroom.ServiceDefaults;

if (args.Contains(ControlPlaneModules.MigrateCommand, StringComparer.Ordinal))
{
    await ControlPlaneModules.MigrateAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAccessTokenAuthentication(Audiences.ControlPlane, [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations]);
builder.AddCellTokenAuthentication();

// The application role's connection pool and the unit of work.
builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
builder.AddControlPlaneModules();

var app = builder.Build();

app.UseDefaultErrorHandling();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapControlPlaneModules();

await app.RunAsync();
