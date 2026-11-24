using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Application.Epoch;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// The cell epoch (docs/adr/0011, "Cell epoch"; docs/contracts/phase-2.md, "Cell epoch"): <c>Welcome</c> carries the
/// registry's epoch, the worker stores it confirmed, and the app role refuses writes (never reads) until it is
/// confirmed and again after an increment until the link has reconnected.
/// </summary>
[Collection(LinkWorkerCollection.Name)]
public sealed class CellEpochTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private const string HubPath = "/hubs/realtime";

    private readonly FakeTimeProvider _gateTime = new(DateTimeOffset.UtcNow);
    private readonly List<WebApplication> _cellApps = [];
    private WebApplication _controlPlane = null!;
    private GrpcChannel _channel = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);
        var server = _controlPlane.GetTestServer();
        _channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _cellApps)
        {
            await app.DisposeAsync();
        }

        _channel.Dispose();
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Welcome_CarriesTheRegistrysEpochAndAnIncrementEndsTheOpenStreamAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var first = await ConnectWelcomedAsync(cell);
        first.ResponseStream.Current.Welcome.Epoch.Should().Be(1UL);

        (await Epochs.IncrementAsync(cell.CellId, CancellationToken)).GetValueOrThrow().Should().Be(2);

        var read = () => first.ResponseStream.MoveNext(CancellationToken);
        var ended = (await read.Should().ThrowAsync<RpcException>()).Which;
        ended.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ended.Status.Detail.Should().Be("cell.epoch_changed");
        using var second = await ConnectWelcomedAsync(cell);
        second.ResponseStream.Current.Welcome.Epoch.Should().Be(2UL);
    }

    [Fact]
    public async Task Writes_BeforeTheEpochIsKnown_AreRefusedWithAStableCodeAndReadsAreNotAsync()
    {
        var client = await StartCellAppAsync("unknown" + Guid.CreateVersion7().ToString("N")[^8..]);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, "/api/v1/things"), CancellationToken);
            await ShouldBeFencedAsync(response);
        }

        using var read = await client.GetAsync(new Uri("/api/v1/things", UriKind.Relative), CancellationToken);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var negotiate = await client.PostAsync(new Uri(HubPath + "/negotiate", UriKind.Relative), content: null, CancellationToken);
        negotiate.StatusCode.Should().Be(HttpStatusCode.OK, "the hub is exempt: its negotiate changes nothing");
    }

    [Fact]
    public async Task Writes_AfterAnIncrement_AreRefusedUntilTheLinkReconnectsWithTheNewEpochAsync()
    {
        // The worker's clock is fake, so it does not reconnect before the test lets it.
        var linkTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database, linkTime);
        var client = await StartCellAppAsync(cell.CellId);
        using (var before = await client.PostAsync(new Uri("/api/v1/things", UriKind.Relative), content: null, CancellationToken))
        {
            await ShouldBeFencedAsync(before);
        }

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(async () => await StoredEpochAsync(cell.CellId) == (1, true));
            (await WriteStatusAsync(client)).Should().Be(HttpStatusCode.NoContent);

            (await Epochs.IncrementAsync(cell.CellId, CancellationToken)).GetValueOrThrow().Should().Be(2);

            await WaitUntilAsync(async () => await StoredEpochAsync(cell.CellId) == (1, false));
            (await WriteStatusAsync(client)).Should().Be(HttpStatusCode.ServiceUnavailable);
            using (var read = await client.GetAsync(new Uri("/api/v1/things", UriKind.Relative), CancellationToken))
            {
                read.StatusCode.Should().Be(HttpStatusCode.OK, "reads are never fenced");
            }

            // The backoff runs out: the worker reconnects and learns the new epoch.
            linkTime.Advance(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(async () => await StoredEpochAsync(cell.CellId) == (2, true));
            (await WriteStatusAsync(client)).Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task AppRole_CannotConfirmTheEpochItselfAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(database.ApplicationConnectionString());
        await using var command = dataSource.CreateCommand(
            "INSERT INTO control_plane_connector.cell_epoch (cell_id, epoch, confirmed, updated_at) VALUES ('forged', 1, true, now())");

        var insert = () => command.ExecuteNonQueryAsync(CancellationToken);

        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private CellEpochService Epochs => _controlPlane.Services.GetRequiredService<CellEpochService>();

    private static async Task ShouldBeFencedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("code").GetString()
            .Should().Be("cell.epoch_unconfirmed");
    }

    // The gate keeps an answer for two seconds: move its clock past that first.
    private async Task<HttpStatusCode> WriteStatusAsync(HttpClient client)
    {
        _gateTime.Advance(TimeSpan.FromSeconds(3));
        using var response = await client.PostAsync(new Uri("/api/v1/things", UriKind.Relative), content: null, CancellationToken);
        return response.StatusCode;
    }

    // The app role's fence as the cell host wires it, in front of a write, a read and the hub's negotiate.
    private async Task<HttpClient> StartCellAppAsync(string cellId)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration["Cell:Id"] = cellId;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Services.AddSingleton<TimeProvider>(_gateTime);
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Services.AddCellEpochFence();
        var app = builder.Build();
        app.UseCellEpochFence(HubPath);
        app.MapMethods("/api/v1/things", ["POST", "PUT", "PATCH", "DELETE"], () => Results.NoContent());
        app.MapGet("/api/v1/things", () => Results.Ok());
        app.MapPost(HubPath + "/negotiate", () => Results.Ok());
        await app.StartAsync(CancellationToken);
        _cellApps.Add(app);
        return app.GetTestClient();
    }

    private async Task<(long Epoch, bool Confirmed)?> StoredEpochAsync(string cellId)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand("SELECT epoch, confirmed FROM control_plane_connector.cell_epoch WHERE cell_id = $1");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        return await reader.ReadAsync(CancellationToken) ? (reader.GetInt64(0), reader.GetBoolean(1)) : null;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 150 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    private async Task<AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse>> ConnectWelcomedAsync(TestCell cell)
    {
        var token = await cell.Credentials.GetAccessTokenAsync(CancellationToken);
        var call = new CellLinkService.CellLinkServiceClient(_channel)
            .Connect(new Metadata { { "Authorization", "Bearer " + token } }, cancellationToken: CancellationToken);
        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = cell.CellId, Version = "1" } }, CancellationToken);
        (await call.ResponseStream.MoveNext(CancellationToken)).Should().BeTrue();
        return call;
    }
}
