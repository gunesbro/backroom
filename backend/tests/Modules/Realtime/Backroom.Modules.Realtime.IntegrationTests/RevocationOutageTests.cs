using System.Diagnostics;
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
/// A cell whose Valkey does not exist (docs/contracts/phase-1.md, "Identity and tokens"): by default the revocation check fails closed,
/// so a request that cannot be checked gets <c>503</c> with <c>Retry-After</c> quickly (never <c>401</c>, which would sign the person
/// out); with <c>Realtime:RevocationFailClosed=false</c> the cell serves it. Revoking itself fails, so the link delivers the message again.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class RevocationOutageTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey), IDisposable
{
    private const string DeadValkey = "127.0.0.1:1";

    private readonly RSA _rsa = RSA.Create(2048);

    protected override SecurityKey? ExtraSigningKey => new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "test-key" };

    public void Dispose() => _rsa.Dispose();

    [Fact]
    public async Task Request_WhenValkeyIsDown_IsAnswered503QuicklyWithRetryAfterAsync()
    {
        await using var node = await DeadNodeAsync(failClosed: true);
        var token = Token(Guid.NewGuid(), "s-1");

        // The first request warms the node up (JIT, connection); the next ones must be quick.
        using var warmUp = await GetAsync(node, token);
        var stopwatch = Stopwatch.StartNew();
        using var response = await GetAsync(node, token);
        stopwatch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "a revoked session must not be served on a guess, and 401 would sign the person out");
        response.Headers.RetryAfter.Should().NotBeNull();
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!["code"]!.GetValue<string>().Should().Be("realtime.revocation_check_unavailable");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "a down Valkey must not make requests wait");
        node.Logs.Entries.Should().NotContain(entry => entry.Contains(token, StringComparison.Ordinal), "tokens are never logged");
    }

    [Fact]
    public async Task Connect_WhenValkeyIsDown_IsRefusedAsync()
    {
        await using var node = await DeadNodeAsync(failClosed: true);
        await using var client = HubClient.Create(node, Token(Guid.NewGuid(), "s-1"));

        await Assert.ThrowsAnyAsync<Exception>(client.StartAsync);
    }

    [Fact]
    public async Task Request_WhenValkeyIsDownAndTheCellFailsOpen_IsServedAsync()
    {
        await using var node = await DeadNodeAsync(failClosed: false);

        using var response = await GetAsync(node, Token(Guid.NewGuid(), "s-1"));

        response.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_WithoutATokenOrWithABadOne_IsNotAffectedByTheOutageAsync()
    {
        await using var node = await DeadNodeAsync(failClosed: true);

        using var anonymous = await node.Client.GetAsync(new Uri($"/api/v1/orgs/{Guid.NewGuid()}/channels", UriKind.Relative), CancellationToken);
        using var forged = await GetAsync(node, "not.a.token");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "authentication answers before the revocation check is asked anything");
        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_WhenValkeyIsDown_FailsSoTheLinkDeliversTheMessageAgainAsync()
    {
        await using var node = await DeadNodeAsync(failClosed: true);
        var now = DateTimeOffset.UtcNow;

        var revoke = () => node.Services.GetRequiredService<ISessionRevocation>().RevokeAsync(
            new SessionRevocation(Guid.NewGuid(), "s-1", now, now.AddMinutes(5)), CancellationToken);

        await revoke.Should().ThrowAsync<Exception>("the control plane must keep the message until the revocation is stored and announced");
    }

    private Task<CellNode> DeadNodeAsync(bool failClosed) =>
        CellNode.StartAsync(
            Database,
            Keycloak.Url,
            CellId,
            DeadValkey,
            new Dictionary<string, string?>
            {
                ["Realtime:RevocationFailClosed"] = failClosed ? "true" : "false",
                ["Realtime:CommandTimeout"] = "00:00:00.500",
            },
            ExtraSigningKey,
            cancellationToken: CancellationToken);

    private string Token(Guid account, string sessionId)
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
                ["iat"] = now.AddSeconds(-1).ToUnixTimeSeconds(),
            },
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }

    private static Task<HttpResponseMessage> GetAsync(CellNode node, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/orgs/{Guid.NewGuid()}/channels", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return node.Client.SendAsync(request, CancellationToken);
    }
}
