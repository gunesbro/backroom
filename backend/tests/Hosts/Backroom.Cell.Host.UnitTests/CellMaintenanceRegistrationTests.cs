using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Backroom.Cell.Host.UnitTests;

/// <summary>Which roles of the cell host run the cross-organization maintenance jobs (docs/adr/0010).</summary>
public sealed class CellMaintenanceRegistrationTests
{
    [Fact]
    public void AddCellModules_ForTheWorker_RegistersTheRunnerAndBothInvitationJobs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["MaintenanceJobs:ConnectionString"] = "Host=db;Username=backroom_maintenance";

        builder.AddCellModules(CellRole.Worker);

        builder.Services.Should().Contain(descriptor => descriptor.ImplementationType == typeof(MaintenanceJobRunner));
        using var provider = builder.Services.BuildServiceProvider();
        var jobs = provider.GetServices<IMaintenanceJob>().ToList();
        jobs.Select(job => job.Name).Should().BeEquivalentTo(["organization.invitation-expiry", "organization.invitation-retention"]);
        jobs.Should().OnlyContain(job => job.BatchSize == 250);
    }

    [Fact]
    public void AddCellModules_ForTheApiRole_RegistersNoRunnerAndNoJobs()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddCellModules(CellRole.App);

        builder.Services.Should().NotContain(descriptor => descriptor.ImplementationType == typeof(MaintenanceJobRunner));
        builder.Services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IMaintenanceJob));
    }
}
