using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Npgsql;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// The cell's end of the link (ControlPlaneConnector) against the control plane on a test server:
/// connecting, the single-holder lease, and reconnecting.
/// </summary>
[Collection(LinkWorkerCollection.Name)]
public sealed class CellLinkWorkerTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly List<GrpcChannel> _channels = [];
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);

    public async ValueTask DisposeAsync()
    {
        _channels.ForEach(channel => channel.Dispose());
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Worker_WhenTheCellIsRegistered_ConnectsItsLinkAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database);

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(() => cell.Link.IsConnected && Sessions.IsConnected(cell.CellId));
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task Worker_WhileAnotherHoldsTheLease_WaitsAndThenConnectsAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database);
        await using var dataSource = LinkLease.CreateDataSource(database.ApplicationConnectionString());
        await using var other = (await LinkLease.TryAcquireAsync(dataSource, cell.CellId, CancellationToken))!;
        other.Should().NotBeNull();

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken);
            Sessions.IsConnected(cell.CellId).Should().BeFalse("another worker holds the link");

            await other.DisposeAsync();

            await WaitUntilAsync(() => cell.Link.IsConnected);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task Worker_WhenItsStreamIsReplaced_ReconnectsAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database);
        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(() => cell.Link.IsConnected);

            // Another stream with the cell's token replaces the worker's one; the worker then replaces it back.
            using var intruder = await ConnectAsync(cell);
            await WaitUntilAsync(() => !cell.Link.IsConnected);

            await WaitUntilAsync(() => cell.Link.IsConnected);
            var read = () => intruder.ResponseStream.MoveNext(CancellationToken);
            (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task Worker_WhenTheStreamIsDueForRenewalButTheTokenIsStillCached_DoesNotSpinAsync()
    {
        // Regression: renewal reused the cached token, whose remaining lifetime was zero, and reconnected
        // in a tight loop. Only the worker's clock moves; the token cache keeps real time.
        var linkTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var cell = await TestCell.RegisterAsync(
            _controlPlane, keycloak, CancellationToken, database, linkTime);
        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(() => cell.Link.IsConnected);

            linkTime.Advance(TimeSpan.FromMinutes(10));
            await WaitUntilAsync(() => !cell.Link.IsConnected);
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken);

            // One stream, one renewal attempt (refused: the token cache's real clock says the fresh token
            // is already due on the worker's clock), then backoff on the worker's clock, which stands still.
            cell.Link.Attempts.Should().BeLessThanOrEqualTo(2, "a token too close to expiry is a failure with backoff, not a reconnect");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public void Backoff_FirstRetry_IsJitteredBetweenOneAndTwoSeconds()
    {
        // Regression: the first retry was exactly one second, so dropped cells reconnected in step.
        var delays = Enumerable.Range(0, 200).Select(_ => CellLinkWorker.Backoff(1)).ToList();

        delays.Should().AllSatisfy(delay => delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(2)));
        delays.Distinct().Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Backoff_StaysWithinOneAndSixtySeconds()
    {
        var delays = Enumerable.Range(1, 20).Select(CellLinkWorker.Backoff).ToList();

        delays.Should().AllSatisfy(delay => delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Worker_WhenRegisteredOnlyLocallyAndKeycloakRefusesItsCredentials_ReportsUnhealthyWithoutRegisteringAsync()
    {
        // A marker from another environment: the control plane has the cell pending and Keycloak holds no key for it.
        var (cellId, key) = await TestCell.ProvisionAsync(_controlPlane, CancellationToken);
        await using var cell = TestCell.Create(_controlPlane, keycloak, cellId, key, database);
        await cell.Services.GetRequiredService<CellKeyStore>().MarkRegisteredAsync(cellId, CancellationToken);

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(() => cell.Status.Rejection is not null);

            cell.Status.Rejection.Should().Be(CellLinkWorker.CredentialsRejectedReason);
            (await HealthAsync(cell)).Should().Be(HealthStatus.Unhealthy);
            cell.Link.IsConnected.Should().BeFalse();
            (await CellStatusAsync(cellId)).Should().Be("pending", "the cell must not register itself again");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task Worker_WhenTheControlPlaneNoLongerHasTheCellRegistered_ReportsUnhealthyUntilItIsRepairedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database);
        await SetCellStatusAsync(cell.CellId, "pending");

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(() => cell.Status.Rejection is not null);

            cell.Status.Rejection.Should().Be(CellLinkWorker.LinkRejectedReason);
            (await HealthAsync(cell)).Should().Be(HealthStatus.Unhealthy);
            cell.Link.IsConnected.Should().BeFalse();

            // An operator's repair takes effect without a restart.
            await SetCellStatusAsync(cell.CellId, "registered");
            await WaitUntilAsync(() => cell.Link.IsConnected);
            cell.Status.Rejection.Should().BeNull();
            (await HealthAsync(cell)).Should().Be(HealthStatus.Healthy);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    private static async Task<HealthStatus> HealthAsync(TestCell cell) =>
        (await new CellLinkHealthCheck(cell.Status).CheckHealthAsync(new HealthCheckContext(), CancellationToken)).Status;

    private async Task<string?> CellStatusAsync(string cellId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status FROM cell_management.cells WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        return (string?)await command.ExecuteScalarAsync(CancellationToken);
    }

    private async Task SetCellStatusAsync(string cellId, string status)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("UPDATE cell_management.cells SET status = $2 WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        (await command.ExecuteNonQueryAsync(CancellationToken)).Should().Be(1);
    }

    private CellLinkSessions Sessions => _controlPlane.Services.GetRequiredService<CellLinkSessions>();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 150 && !condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        condition().Should().BeTrue();
    }

    private async Task<AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse>> ConnectAsync(TestCell cell)
    {
        var server = _controlPlane.GetTestServer();
        var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        _channels.Add(channel);
        var call = new CellLinkService.CellLinkServiceClient(channel).Connect(
            new Metadata { { "Authorization", "Bearer " + await cell.Credentials.GetAccessTokenAsync(CancellationToken) } },
            cancellationToken: CancellationToken);
        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = cell.CellId, Version = "1" } }, CancellationToken);
        await call.ResponseStream.MoveNext(CancellationToken);
        return call;
    }
}
