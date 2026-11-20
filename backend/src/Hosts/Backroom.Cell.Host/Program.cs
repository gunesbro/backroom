using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Cell.Host;
using Backroom.Modules.Organization.Presentation;
using Backroom.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

var role = CellRoleParser.Parse(builder.Configuration[CellRoleParser.ConfigurationKey]);
if (role == CellRole.Migrate)
{
    await CellModules.MigrateAsync(args);
    return;
}

var cellId = builder.Configuration[CellIdentity.ConfigurationKey]
    ?? throw new InvalidOperationException($"{CellIdentity.ConfigurationKey} is required.");

builder.AddServiceDefaults();

// This cell accepts only tokens issued for it (audience cell-{id}).
builder.AddAccessTokenAuthentication(Audiences.Cell(cellId), [KeycloakClients.Web, KeycloakClients.Desktop]);

// The application role's connection pool and the unit of work.
builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
builder.AddCellModules(role);
// UseRateLimiter needs its services in every role; only the app role maps endpoints that use them.
builder.Services.AddOrganizationEndpoints();

// Role-specific services (endpoints for "app", consumers and jobs for "worker") are registered by the modules.

var app = builder.Build();

app.UseDefaultErrorHandling();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapDefaultEndpoints();
if (role == CellRole.App)
{
    app.MapOrganizationEndpoints();
}

CellHostLog.Starting(app.Logger, role);

await app.RunAsync();
