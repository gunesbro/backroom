using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// The hub's WebSocket on real Kestrel, connected the way a browser does: no negotiate, WebSockets only, the token in the
/// query string. The test server's WebSocket client marks the request as an upgrade itself, so only Kestrel shows whether
/// authentication sees the upgrade and takes the query token.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class KestrelConnectionTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    private const char RecordSeparator = '\u001e';

    [Fact]
    public async Task Connect_OnKestrelWithTheTokenInTheQuery_UpgradesAndCompletesTheHandshakeAsync()
    {
        await using var node = await StartKestrelNodeAsync();
        using var socket = NewSocket();

        await socket.ConnectAsync(HubUri(node, await TokenAsync(Alice)), CancellationToken);

        socket.HttpStatusCode.Should().Be(HttpStatusCode.SwitchingProtocols);
        var handshake = Encoding.UTF8.GetBytes($$"""{"protocol":"messagepack","version":1}{{RecordSeparator}}""");
        await socket.SendAsync(handshake, WebSocketMessageType.Text, endOfMessage: true, CancellationToken);
        var buffer = new byte[256];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(HubClient.Deadline);
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        Encoding.UTF8.GetString(buffer, 0, received.Count).Should().Be($"{{}}{RecordSeparator}", "an empty handshake response has no error");
    }

    [Fact]
    public async Task Connect_OnKestrelWithoutAToken_IsRefusedWith401Async()
    {
        await using var node = await StartKestrelNodeAsync();
        using var socket = NewSocket();

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(HubUri(node, token: null), CancellationToken));

        socket.HttpStatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Task<CellNode> StartKestrelNodeAsync() =>
        CellNode.StartAsync(Database, Keycloak.Url, CellId, Valkey.ConnectionString, kestrel: true, cancellationToken: CancellationToken);

    private static ClientWebSocket NewSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        return socket;
    }

    private static Uri HubUri(CellNode node, string? token)
    {
        var builder = new UriBuilder(new Uri(node.BaseAddress, RealtimeEndpoints.HubPath)) { Scheme = "ws" };
        if (token is not null)
        {
            builder.Query = $"access_token={Uri.EscapeDataString(token)}";
        }

        return builder.Uri;
    }
}
