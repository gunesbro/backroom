using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// The control plane's end of the link (docs/contracts/phase-1.md, "Cell ↔ control plane link"): only a
/// registered cell's own token opens a stream, under its own id.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(KeycloakSignInCollection.Name)]
public sealed class CellLinkTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private WebApplication _controlPlane = null!;
    private GrpcChannel _channel = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private CellLinkService.CellLinkServiceClient Client => new(_channel);

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, _time);
        var server = _controlPlane.GetTestServer();
        _channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Connect_WithTheCellsToken_IsWelcomedAndRecordsTheHeartbeatAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectAsync(cell);

        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = cell.CellId, Version = "1.2.3" } }, CancellationToken);
        var welcomed = await call.ResponseStream.MoveNext(CancellationToken);

        welcomed.Should().BeTrue();
        call.ResponseStream.Current.Welcome.FullSyncIntervalSeconds.Should().Be(900);
        (await CellLinkStateAsync(cell.CellId)).Should().Be(("1.2.3", true));
    }

    [Fact]
    public async Task Connect_WithHelloNamingAnotherCell_IsPermissionDeniedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectAsync(cell);

        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = "another-cell", Version = "1" } }, CancellationToken);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Connect_WithAPersonsToken_IsUnauthenticatedAsync()
    {
        var (token, _) = await keycloak.SignInAsync("alice@example.test", "openid control-plane");
        using var call = Client.Connect(Bearer(token), cancellationToken: CancellationToken);

        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = "a", Version = "1" } }, CancellationToken);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Connect_WithoutHello_EndsAfterTheTimeoutAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectAsync(cell);
        var read = call.ResponseStream.MoveNext(CancellationToken);

        await AdvanceUntilEndedAsync(read, TimeSpan.FromSeconds(1), maxSteps: 60);

        (await ReadFailureAsync(read)).StatusCode.Should().Be(StatusCode.DeadlineExceeded);
    }

    [Fact]
    public async Task Connect_AgainForTheSameCell_ReplacesTheOlderStreamAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var first = await ConnectWelcomedAsync(cell);

        using var second = await ConnectWelcomedAsync(cell);
        var read = () => first.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
    }

    [Fact]
    public async Task Connect_WhenTheTokenExpires_EndsTheStreamAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectWelcomedAsync(cell);
        await WaitUntilConnectedAsync(cell.CellId);
        var read = call.ResponseStream.MoveNext(CancellationToken);

        await AdvanceUntilEndedAsync(read, TimeSpan.FromMinutes(10), maxSteps: 5);

        (await ReadFailureAsync(read)).StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Connect_WithADurableMessage_GetsARetryableNackAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectWelcomedAsync(cell);

        await call.RequestStream.WriteAsync(
            new ConnectRequest { Envelope = new Envelope { MessageId = "0190a000-0000-7000-8000-000000000001" }, MembershipDigest = new MembershipDigest() }, CancellationToken);
        await call.ResponseStream.MoveNext(CancellationToken);

        call.ResponseStream.Current.Nack.Should().BeEquivalentTo(new Nack { MessageId = "0190a000-0000-7000-8000-000000000001", Code = "link.not_ready", Retryable = true });
    }

    [Fact]
    public async Task Connect_WithAnUnknownPayloadType_GetsANonRetryableNackAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectWelcomedAsync(cell);

        await call.RequestStream.WriteAsync(new ConnectRequest { Envelope = new Envelope { MessageId = "m-2" } }, CancellationToken);
        await call.ResponseStream.MoveNext(CancellationToken);

        call.ResponseStream.Current.Nack.Should().BeEquivalentTo(new Nack { MessageId = "m-2", Code = "unsupported_type", Retryable = false });
    }

    [Fact]
    public async Task Connect_WithADurableMessageWithoutId_IsInvalidArgumentAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectWelcomedAsync(cell);

        await call.RequestStream.WriteAsync(new ConnectRequest { MembershipDigest = new MembershipDigest() }, CancellationToken);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Connect_WhenTheCellIsNotRegistered_IsPermissionDeniedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        await using (var connection = new NpgsqlConnection(database.SuperuserConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var command = new NpgsqlCommand("UPDATE cell_management.cells SET status = 'pending' WHERE id = $1", connection);
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.CellId });
            await command.ExecuteNonQueryAsync(CancellationToken);
        }

        // Refused before Hello is read.
        using var call = await ConnectAsync(cell);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Connect_WithABadHello_KeepsTheCellsCurrentStreamAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var current = await ConnectWelcomedAsync(cell);
        using var bad = await ConnectAsync(cell);

        await bad.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = "another-cell", Version = "1" } }, CancellationToken);
        var badRead = () => bad.ResponseStream.MoveNext(CancellationToken);
        await badRead.Should().ThrowAsync<RpcException>();
        await current.RequestStream.WriteAsync(
            new ConnectRequest { Envelope = new Envelope { MessageId = "m-3" }, MembershipDigest = new MembershipDigest() }, CancellationToken);

        (await current.ResponseStream.MoveNext(CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_AfterTheEpochChangedOnAnotherInstance_EndsTheStreamAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectWelcomedAsync(cell);
        // Another control-plane instance incremented it: this instance's sessions were not told.
        await SqlAsync("UPDATE cell_management.cells SET epoch = epoch + 1 WHERE id = $1", cell.CellId);

        _time.Advance(s_heartbeatWriteGap);
        await call.RequestStream.WriteAsync(new ConnectRequest { Heartbeat = new Heartbeat { Version = "1" } }, CancellationToken);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        var ended = (await read.Should().ThrowAsync<RpcException>()).Which;
        ended.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ended.Status.Detail.Should().Be("cell.epoch_changed");
    }

    [Fact]
    public async Task Heartbeat_AfterTheCellStoppedBeingRegistered_IsPermissionDeniedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectWelcomedAsync(cell);
        await SqlAsync("UPDATE cell_management.cells SET status = 'pending' WHERE id = $1", cell.CellId);

        _time.Advance(s_heartbeatWriteGap);
        await call.RequestStream.WriteAsync(new ConnectRequest { Heartbeat = new Heartbeat { Version = "1" } }, CancellationToken);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // CellLinkEndpoint.HeartbeatWriteInterval (20 s) and a little more, so the heartbeat is written.
    private static readonly TimeSpan s_heartbeatWriteGap = TimeSpan.FromSeconds(21);

    private async Task SqlAsync(string sql, string cellId)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private static Metadata Bearer(string token) => new() { { "Authorization", "Bearer " + token } };

    private static async Task<RpcException> ReadFailureAsync(Task<bool> read) =>
        (await ((Func<Task<bool>>)(() => read)).Should().ThrowAsync<RpcException>()).Which;

    // Timers of the fake clock fire on Advance, but only once the server has armed them: advance in
    // small steps (far below the token's 5-minute lifetime) until the read ends, and fail fast otherwise.
    private async Task AdvanceUntilEndedAsync(Task read, TimeSpan step, int maxSteps)
    {
        for (var attempt = 0; attempt < maxSteps && !read.IsCompleted; attempt++)
        {
            _time.Advance(step);
            await Task.WhenAny(read, Task.Delay(100, CancellationToken));
        }

        read.IsCompleted.Should().BeTrue("the stream should have ended");
    }

    private async Task WaitUntilConnectedAsync(string cellId)
    {
        var sessions = _controlPlane.Services.GetRequiredService<CellLinkSessions>();
        for (var attempt = 0; attempt < 100 && !sessions.IsConnected(cellId); attempt++)
        {
            await Task.Delay(50, CancellationToken);
        }

        sessions.IsConnected(cellId).Should().BeTrue();
    }

    private async Task<AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse>> ConnectAsync(TestCell cell) =>
        Client.Connect(Bearer(await cell.Credentials.GetAccessTokenAsync(CancellationToken)), cancellationToken: CancellationToken);

    private async Task<AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse>> ConnectWelcomedAsync(TestCell cell)
    {
        var call = await ConnectAsync(cell);
        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = cell.CellId, Version = "1" } }, CancellationToken);
        await call.ResponseStream.MoveNext(CancellationToken);
        return call;
    }

    private async Task<(string? Version, bool HasHeartbeat)> CellLinkStateAsync(string cellId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT version, last_heartbeat_at IS NOT NULL FROM cell_management.cells WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        await reader.ReadAsync(CancellationToken);
        return (await reader.IsDBNullAsync(0, CancellationToken) ? null : reader.GetString(0), reader.GetBoolean(1));
    }
}
