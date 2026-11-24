using Backroom.Cell.Host;

var builder = WebApplication.CreateBuilder(args);

var role = CellRoleParser.Parse(builder.Configuration[CellRoleParser.ConfigurationKey]);
if (role == CellRole.Migrate)
{
    await CellModules.MigrateAsync(args);
    return;
}

var app = CellApp.Build(builder, role);
CellHostLog.Starting(app.Logger, role);

await app.RunAsync();
