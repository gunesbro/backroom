using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Backroom.Modules.CellManagement.Application.Registration;

namespace Backroom.Modules.CellManagement.Infrastructure;

/// <summary>
/// Development only (registered only in that environment): adds the AppHost's cells as pending cells
/// with their registration keys, so they can register on their first start.
/// </summary>
internal sealed class DevelopmentCellSeeder(CellRegistry registry, IOptions<DevelopmentCellOptions> options, TimeProvider timeProvider) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var cell in options.Value.DevelopmentCells)
        {
            var newCell = new NewCell(cell.Id, cell.Name, "shared", cell.Region, cell.PublicUrl ?? new Uri("http://localhost/"));
            if (!newCell.IsValid() || !RegistrationKeys.HasValidShape(cell.RegistrationKey))
            {
                throw new InvalidOperationException($"Development cell '{cell.Id}' is not configured correctly.");
            }

            await registry.SeedDevelopmentCellAsync(
                newCell, RegistrationKeys.Hash(cell.RegistrationKey), timeProvider.GetUtcNow() + RegistrationKeys.Lifetime, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
