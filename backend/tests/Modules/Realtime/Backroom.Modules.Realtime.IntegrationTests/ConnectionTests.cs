using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>The hub's connection: who may connect, how it ends, which protocol it speaks and what it leaves in the logs.</summary>
[Collection(CellCollection.Name)]
public sealed class ConnectionTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task Connect_WithoutAToken_IsRefusedAsync()
    {
        await using var client = HubClient.Create(NodeA, token: string.Empty);

        await Assert.ThrowsAnyAsync<Exception>(client.StartAsync);
    }

    [Fact]
    public async Task Connect_WithATokenForAnotherCell_IsRefusedAsync()
    {
        var other = await Keycloak.CreateCellScopeAsync();
        var token = (await Keycloak.SignInAsync(Alice, $"openid cell:{other}")).AccessToken;
        await using var client = HubClient.Create(NodeA, token);

        await Assert.ThrowsAnyAsync<Exception>(client.StartAsync);
    }

    [Fact]
    public async Task Connect_WithTheJsonProtocol_IsRefusedAsync()
    {
        await using var client = HubClient.Create(NodeA, await TokenAsync(Alice), json: true);

        var failure = await Assert.ThrowsAnyAsync<Exception>(client.StartAsync);

        failure.Message.Should().Contain("json");
    }

    [Fact]
    public async Task Connect_WithTheMessagePackProtocol_IsAcceptedAsync()
    {
        await using var client = await ConnectAsync(NodeA, Alice);

        client.State.Should().Be(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected);
    }

    [Fact]
    public async Task Connect_WhenTheTokenExpires_ClosesTheConnectionAsync()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        await using var node = await CellNode.StartAsync(
            Database, Keycloak.Url, CellId, Valkey.ConnectionString, extraSigningKey: new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "test-key" }, cancellationToken: CancellationToken);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Keycloak.Url,
            Audience = $"cell-{CellId}",
            Claims = new Dictionary<string, object>
            {
                [KeycloakClaims.Subject] = (await AccountAsync(Alice)).ToString("D"),
                [KeycloakClaims.SessionId] = Guid.NewGuid().ToString("D"),
                [KeycloakClaims.TokenType] = KeycloakClaims.AccessTokenType,
                [KeycloakClaims.AuthorizedParty] = KeycloakClients.Web,
            },
            Expires = DateTime.UtcNow.AddSeconds(3),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        await using var client = HubClient.Create(node, token);

        await client.StartAsync();
        client.Closed.IsCompleted.Should().BeFalse("the token is still valid");

        // The server closes the connection when the token expires; the client then reconnects with a fresh one.
        await client.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
    }

    [Fact]
    public async Task Connect_WithTheTokenInTheQueryString_LeavesItOutOfEveryLogAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var token = await TokenAsync(Alice);
        var client = await ConnectAsync(NodeA, Alice);
        await client.SubscribeConversationAsync(organizationId, channelId);
        await SendMessageAsync(NodeB, organizationId, channelId, Alice, "this text must not be logged either");
        await client.NextMessageAsync();

        foreach (var node in new[] { NodeA, NodeB })
        {
            node.Logs.Entries.Should().NotBeEmpty();
            node.Logs.Entries.Should().NotContain(entry => entry.Contains(token, StringComparison.Ordinal));
            node.Logs.Entries.Should().NotContain(entry => entry.Contains("access_token", StringComparison.OrdinalIgnoreCase));
            node.Logs.Entries.Should().NotContain(entry => entry.Contains("this text must not be logged", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Request_WithTheTokenInTheQueryStringOutsideTheHub_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var token = await TokenAsync(Alice);

        using var query = await NodeA.Client.GetAsync(new Uri($"/api/v1/orgs/{organizationId}/channels?access_token={token}", UriKind.Relative), CancellationToken);
        using var header = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/orgs/{organizationId}/channels", UriKind.Relative));
        header.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var withHeader = await NodeA.Client.SendAsync(header, CancellationToken);

        query.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withHeader.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metrics_ForConnectionsAndSubscriptions_CountThemAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        using var listener = new MetricCounter();

        var client = await ConnectAsync(NodeA, Alice);
        await client.SubscribeConversationAsync(organizationId, channelId);
        await SendMessageAsync(NodeA, organizationId, channelId, Alice, "hi");
        await client.NextMessageAsync();

        listener.Increases("backroom.realtime.connections").Should().Be(1);
        listener.Increases("backroom.realtime.subscriptions").Should().Be(1);
        listener.Total("backroom.realtime.publishes").Should().BeGreaterThanOrEqualTo(1);
        listener.Total("backroom.realtime.publish_failures").Should().Be(0);
    }

    [Theory]
    [InlineData(Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling)]
    [InlineData(Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents)]
    public async Task Connect_WithATransportOtherThanWebSockets_IsRefusedAsync(Microsoft.AspNetCore.Http.Connections.HttpTransportType transport)
    {
        await using var client = HubClient.Create(NodeA, await TokenAsync(Alice), transport: transport);

        await Assert.ThrowsAnyAsync<Exception>(client.StartAsync);
    }
}
