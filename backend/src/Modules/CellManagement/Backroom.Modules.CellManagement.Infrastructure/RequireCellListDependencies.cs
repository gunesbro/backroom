using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Backroom.Modules.CellManagement.Application.Listing;

namespace Backroom.Modules.CellManagement.Infrastructure;

/// <summary>Resolves the cell list at startup, so a missing dependency stops the host instead of failing each request.</summary>
internal sealed class RequireCellListDependencies(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<CellListService>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
