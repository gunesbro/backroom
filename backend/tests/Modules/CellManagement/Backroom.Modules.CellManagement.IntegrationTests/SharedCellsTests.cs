using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.CellManagement.Application;
using Backroom.Modules.CellManagement.Application.Provisioning;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// The public list of shared cells and its limits (docs/contracts/phase-1.md, "Audience per target" and
/// <c>GET /api/v1/cells/shared</c>).
/// </summary>
[Trait("Category", "Privacy")]
[Collection(SharedCellCountCollection.Name)]
public sealed class SharedCellsTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task SharedCells_WithoutSignIn_ListRegisteredSharedCellsOnlyAsync()
    {
        // A customer-run cell must never be listed: the list would tell which customers run their own servers.
        await using var registeredShared = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var (pendingShared, _) = await TestCell.ProvisionAsync(_controlPlane, CancellationToken);
        var hybrid = await RegisterAsync("hybrid");
        var dedicated = await RegisterAsync("dedicated");

        using var client = _controlPlane.GetTestClient();
        using var response = await client.GetAsync(new Uri("/api/v1/cells/shared", UriKind.Relative), CancellationToken);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;
        var ids = body["cells"]!.AsArray().Select(cell => cell!["id"]!.GetValue<string>()).ToList();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=300");
        ids.Should().Contain(registeredShared.CellId).And.NotContain([pendingShared, hybrid, dedicated]);
        body["cells"]!.AsArray().Should().OnlyContain(cell => cell!.AsObject().Count == 1, "only the id is public");
    }

    [Theory]
    [InlineData("shared", 16, true)]
    [InlineData("shared", 17, false)]
    [InlineData("hybrid", 40, true)]
    public async Task ProvisionCell_WithAnIdOfLength_IsAcceptedOnlyWithinTheSharedLimitAsync(string type, int length, bool accepted)
    {
        var id = ("t" + Guid.CreateVersion7().ToString("N")[^15..] + new string('a', 40))[..length];

        var result = await Provisioning().ProvisionAsync(
            new NewCell(id, "Test cell", type, "eu-test", new Uri("https://cell.example.test")), CancellationToken);

        result.IsSuccess.Should().Be(accepted);
        if (!accepted)
        {
            result.Error.Should().Be(CellManagementErrors.CellInvalid);
        }
    }

    [Fact]
    public async Task ProvisionCell_WhenTheSharedCellLimitIsReached_RefusesSharedCellsOnlyAsync()
    {
        var registry = _controlPlane.Services.GetRequiredService<ICellRegistry>();
        var atLimit = new SharedCellOptions { Max = await registry.CountSharedCellsAsync(CancellationToken) };
        var provisioning = new CellProvisioningService(registry, atLimit, TimeProvider.System, NullLogger<CellProvisioningService>.Instance);

        var shared = await provisioning.ProvisionAsync(NewTestCell("shared"), CancellationToken);
        var hybrid = await provisioning.ProvisionAsync(NewTestCell("hybrid"), CancellationToken);

        shared.Error.Should().Be(CellManagementErrors.SharedCellLimit);
        hybrid.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SharedCellCount_WithAnExpiredPendingCell_LeavesItsSlotFreeAsync()
    {
        // An abandoned provisioning must not use up one of the 20 slots for good.
        var registry = _controlPlane.Services.GetRequiredService<ICellRegistry>();
        var before = await registry.CountSharedCellsAsync(CancellationToken);
        var past = new FakeTimeProvider(DateTimeOffset.UtcNow - RegistrationKeys.Lifetime - TimeSpan.FromMinutes(1));
        var pastRegistry = ActivatorUtilities.CreateInstance<CellRegistry>(_controlPlane.Services, past);
        var provisioning = new CellProvisioningService(pastRegistry, new SharedCellOptions { Max = 100000 }, past, NullLogger<CellProvisioningService>.Instance);

        (await provisioning.ProvisionAsync(NewTestCell("shared"), CancellationToken)).IsSuccess.Should().BeTrue();
        var afterExpired = await registry.CountSharedCellsAsync(CancellationToken);
        await TestCell.ProvisionAsync(_controlPlane, CancellationToken);
        var afterValid = await registry.CountSharedCellsAsync(CancellationToken);

        afterExpired.Should().Be(before, "its key expired before it registered");
        afterValid.Should().Be(before + 1, "a pending cell holds a slot while its key can be used");
    }

    [Fact]
    public async Task SharedCellIds_WithALimit_ReturnAtMostThatManyAsync()
    {
        await using var first = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        await using var second = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);

        var ids = await _controlPlane.Services.GetRequiredService<ICellRegistry>().SharedCellIdsAsync(1, CancellationToken);

        ids.Should().ContainSingle();
    }

    [Fact]
    public async Task Startup_OutsideDevelopmentWithMoreThan20SharedCells_FailsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Staging" });
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["CellManagement:Keycloak:AdminClientSecret"] = "test-secret";
        builder.Configuration["CellManagement:SharedCells:Max"] = "21";
        builder.AddCellManagement();
        await using var app = builder.Build();

        var start = () => app.StartAsync(CancellationToken);

        await start.Should().ThrowAsync<OptionsValidationException>().WithMessage("*at most 20*");
    }

    private CellProvisioningService Provisioning() => _controlPlane.Services.GetRequiredService<CellProvisioningService>();

    private static NewCell NewTestCell(string type) =>
        new("t" + Guid.CreateVersion7().ToString("N")[^10..], "Test cell", type, "eu-test", new Uri("https://cell.example.test"));

    private async Task<string> RegisterAsync(string type)
    {
        var cell = NewTestCell(type);
        var key = (await Provisioning().ProvisionAsync(cell, CancellationToken)).GetValueOrThrow().RegistrationKey;
        await using var registered = TestCell.Create(_controlPlane, keycloak, cell.Id, key);
        (await registered.Worker.RegisterAsync(CancellationToken)).Should().BeTrue();
        return cell.Id;
    }
}
