using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// Revoked sessions in a cell with two app nodes on one real Valkey (docs/contracts/phase-1.md, "Identity and tokens" and flow 5): a
/// revoked token gets 401 on every node and cannot connect to the hub, connections of the session receive <c>SessionRevoked</c> and are
/// closed on both nodes, other sessions are untouched, and a revocation ends with its <c>keep_until</c>. The test signs its own tokens
/// (with the sid and iat it needs) with a second key both nodes trust; one test uses a real Keycloak token.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class SessionRevocationTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey), IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    protected override SecurityKey? ExtraSigningKey => new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "test-key" };

    public void Dispose() => _rsa.Dispose();

    [Fact]
    public async Task Revoke_ASession_RefusesItsTokensOnBothNodesAndNoOtherAsync()
    {
        var account = Guid.NewGuid();
        var revoked = Token(account, "s-revoked");
        var sibling = Token(account, "s-sibling");
        var stranger = Token(Guid.NewGuid(), "s-stranger");
        (await StatusAsync(NodeA, revoked)).Should().NotBe(HttpStatusCode.Unauthorized, "it is valid until it is revoked");

        await RevokeAsync(account, "s-revoked");

        foreach (var node in new[] { NodeA, NodeB })
        {
            using var response = await GetAsync(node, revoked);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.Should().ContainSingle().Which.ToString().Should().Contain("invalid_token");
            JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!["code"]!.GetValue<string>().Should().Be("identity.session_revoked");
            (await StatusAsync(node, sibling)).Should().NotBe(HttpStatusCode.Unauthorized, "another session of the account is not revoked");
            (await StatusAsync(node, stranger)).Should().NotBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Revoke_EverySession_RefusesTokensIssuedBeforeItAndAcceptsALaterSignInAsync()
    {
        var account = Guid.NewGuid();
        var revokedAt = DateTimeOffset.UtcNow;
        var before = Token(account, "s-before", issuedAt: revokedAt.AddMinutes(-2));
        var sameSecond = Token(account, "s-same-second", issuedAt: revokedAt);
        var after = Token(account, "s-after", issuedAt: revokedAt.AddSeconds(2));

        await RevokeAsync(account, sessionId: null, revokedAt);

        (await StatusAsync(NodeA, before)).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(NodeB, sameSecond)).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(NodeA, after)).Should().NotBe(HttpStatusCode.Unauthorized, "signing in again after signing out everywhere works");
        (await StatusAsync(NodeB, after)).Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Connect_WithARevokedSession_IsRefusedAsync()
    {
        var account = Guid.NewGuid();
        await RevokeAsync(account, "s-revoked");

        foreach (var node in new[] { NodeA, NodeB })
        {
            await using var refused = HubClient.Create(node, Token(account, "s-revoked"));
            await Assert.ThrowsAnyAsync<Exception>(refused.StartAsync);
            await using var allowed = HubClient.Create(node, Token(account, "s-other"));
            await allowed.StartAsync();
            allowed.State.Should().Be(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected);
        }
    }

    [Fact]
    public async Task Revoke_ASession_TellsAndClosesItsConnectionsOnBothNodesAsync()
    {
        var account = Guid.NewGuid();
        await using var onA = await ConnectWithAsync(NodeA, Token(account, "s-revoked"));
        await using var onB = await ConnectWithAsync(NodeB, Token(account, "s-revoked"));
        await using var otherSession = await ConnectWithAsync(NodeB, Token(account, "s-kept"));
        await using var otherAccount = await ConnectWithAsync(NodeA, Token(Guid.NewGuid(), "s-revoked"));

        // The cell's worker applies it on its own node; every app node hears of it through Valkey.
        await RevokeAsync(account, "s-revoked");

        (await onA.NextSessionRevokedAsync()).Reason.Should().Be("session_ended", "the client learns why it is signed out before the connection closes");
        (await onB.NextSessionRevokedAsync()).Reason.Should().Be("session_ended");
        await onA.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
        await onB.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
        otherSession.Closed.IsCompleted.Should().BeFalse("another session of the account stays");
        otherAccount.Closed.IsCompleted.Should().BeFalse("the same session id of another account is another session");
    }

    [Fact]
    public async Task Revoke_EverySession_ClosesTheConnectionsOfOlderTokensOnlyAsync()
    {
        var account = Guid.NewGuid();
        var revokedAt = DateTimeOffset.UtcNow;
        await using var older = await ConnectWithAsync(NodeA, Token(account, "s-older", issuedAt: revokedAt.AddMinutes(-2)));
        await using var newer = await ConnectWithAsync(NodeB, Token(account, "s-newer", issuedAt: revokedAt.AddSeconds(2)));

        await RevokeAsync(account, sessionId: null, revokedAt);

        await older.NextSessionRevokedAsync();
        await older.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
        newer.Closed.IsCompleted.Should().BeFalse("it was issued after the revocation");
    }

    [Fact]
    public async Task Revoke_ANodeStartedAfterIt_RefusesTheTokenFromValkeyAsync()
    {
        var account = Guid.NewGuid();
        await RevokeAsync(account, "s-revoked");
        await using var late = await CellNode.StartAsync(
            Database, Keycloak.Url, CellId, Valkey.ConnectionString, Settings, ExtraSigningKey, cancellationToken: CancellationToken);

        (await StatusAsync(late, Token(account, "s-revoked"))).Should().Be(HttpStatusCode.Unauthorized, "it never heard the announcement; Valkey holds the revocation");
    }

    [Fact]
    public async Task Revoke_ForACellOfAnotherName_DoesNotReachThisCellAsync()
    {
        var account = Guid.NewGuid();
        await using var other = await CellNode.StartAsync(
            Database, Keycloak.Url, "other-" + Guid.NewGuid().ToString("N")[..8], Valkey.ConnectionString, Settings, ExtraSigningKey, cancellationToken: CancellationToken);
        var revocation = new SessionRevocation(account, "s-revoked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        await other.Services.GetRequiredService<ISessionRevocation>().RevokeAsync(revocation, CancellationToken);

        (await StatusAsync(NodeA, Token(account, "s-revoked"))).Should().NotBe(HttpStatusCode.Unauthorized, "cells that share a Valkey server keep their revocations apart");
    }

    [Fact]
    public async Task Revoke_WhenKeepUntilPasses_TheSessionIsAcceptedAgainAsync()
    {
        var account = Guid.NewGuid();
        var token = Token(account, "s-short");
        await RevokeAsync(account, "s-short", DateTimeOffset.UtcNow, keepFor: TimeSpan.FromSeconds(2));
        (await StatusAsync(NodeA, token)).Should().Be(HttpStatusCode.Unauthorized);

        // keep_until is the end of the revocation: Valkey drops the key and the nodes drop their copies.
        await WaitUntilAsync(async () => await StatusAsync(NodeA, token) != HttpStatusCode.Unauthorized);
        await WaitUntilAsync(async () => await StatusAsync(NodeB, token) != HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_AnAlreadyExpiredRevocation_ChangesNothingAsync()
    {
        var account = Guid.NewGuid();

        await RevokeAsync(account, "s-late", DateTimeOffset.UtcNow.AddMinutes(-10), keepFor: TimeSpan.FromMinutes(5));

        (await StatusAsync(NodeA, Token(account, "s-late"))).Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_TwiceOrWithAnAnnouncementAlreadyHeard_IsHarmlessAsync()
    {
        var account = Guid.NewGuid();
        await RevokeAsync(account, "s-twice");
        await RevokeAsync(account, "s-twice");
        await RevokeAsync(account, "s-twice");

        (await StatusAsync(NodeA, Token(account, "s-twice"))).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(NodeA, Token(account, "s-once-more"))).Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_WithAnInvalidRevocation_IsRefusedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var account = Guid.NewGuid();
        SessionRevocation[] invalid =
        [
            new(Guid.Empty, "s", now, now.AddMinutes(5)),
            new(account, string.Empty, now, now.AddMinutes(5)),
            new(account, new string('s', 129), now, now.AddMinutes(5)),
            new(account, "s", now, now.AddMinutes(-1)),
            new(account, "s", now, now.AddDays(2)),
        ];
        var revocation = NodeA.Services.GetRequiredService<ISessionRevocation>();

        foreach (var item in invalid)
        {
            var revoke = () => revocation.RevokeAsync(item, CancellationToken);
            await revoke.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task Check_WithARealKeycloakToken_RefusesTheRevokedSessionAndAcceptsANewSignInAsync()
    {
        var token = await TokenAsync(Alice);
        var parsed = new JsonWebToken(token);
        var account = Guid.Parse(parsed.GetPayloadValue<string>("sub"));
        var sessionId = parsed.GetPayloadValue<string>("sid");
        (await StatusAsync(NodeA, token)).Should().NotBe(HttpStatusCode.Unauthorized);

        await RevokeAsync(account, sessionId);

        (await StatusAsync(NodeA, token)).Should().Be(HttpStatusCode.Unauthorized, "Keycloak's tokens carry the sid and sub the check reads");
        (await StatusAsync(NodeB, token)).Should().Be(HttpStatusCode.Unauthorized);
        var newSession = (await Keycloak.SignInAsync(Alice, $"openid cell:{CellId}")).AccessToken;
        new JsonWebToken(newSession).GetPayloadValue<string>("sid").Should().NotBe(sessionId);
        (await StatusAsync(NodeA, newSession)).Should().NotBe(HttpStatusCode.Unauthorized, "a new sign-in is a new session");
    }

    private string Token(Guid account, string sessionId, DateTimeOffset? issuedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Keycloak.Url,
            Audience = $"cell-{CellId}",
            Claims = new Dictionary<string, object>
            {
                [KeycloakClaims.Subject] = account.ToString("D"),
                [KeycloakClaims.SessionId] = sessionId,
                [KeycloakClaims.TokenType] = KeycloakClaims.AccessTokenType,
                [KeycloakClaims.AuthorizedParty] = KeycloakClients.Web,
                ["iat"] = (issuedAt ?? now.AddSeconds(-1)).ToUnixTimeSeconds(),
            },
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }

    // What the cell's worker does when SessionRevoked arrives, on node A's services.
    private Task RevokeAsync(Guid account, string? sessionId, DateTimeOffset? revokedAt = null, TimeSpan? keepFor = null)
    {
        var at = revokedAt ?? DateTimeOffset.UtcNow;
        return NodeA.Services.GetRequiredService<ISessionRevocation>().RevokeAsync(
            new SessionRevocation(account, sessionId, at, at + (keepFor ?? TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(30)))), CancellationToken);
    }

    private static Task<HttpResponseMessage> GetAsync(CellNode node, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/orgs/{Guid.NewGuid()}/channels", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return node.Client.SendAsync(request, CancellationToken);
    }

    private static async Task<HttpStatusCode> StatusAsync(CellNode node, string token)
    {
        using var response = await GetAsync(node, token);
        return response.StatusCode;
    }

    private static async Task<HubClient> ConnectWithAsync(CellNode node, string token)
    {
        var client = HubClient.Create(node, token);
        await client.StartAsync();
        return client;
    }

    // Bounded polling: the condition is checked every 100 ms for at most the test deadline.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(HubClient.Deadline);
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
    }
}
