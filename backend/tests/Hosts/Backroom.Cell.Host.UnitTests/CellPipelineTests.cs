using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Presentation.Authentication;

namespace Backroom.Cell.Host.UnitTests;

/// <summary>
/// The app role's real pipeline (<see cref="CellApp"/>, as Program builds it) on a test server: the epoch fence runs
/// after authentication and authorization, so a caller without a valid token gets 401 and learns nothing, and a
/// signed-in write gets <c>503 cell.epoch_unconfirmed</c>. The cell's database and Valkey are unreachable here: the
/// fence fails closed, and the revocation check is set to fail open so it does not answer first.
/// </summary>
public sealed class CellPipelineTests : IAsyncLifetime
{
    private const string Issuer = "http://localhost:9/realms/backroom";
    private const string CellId = "pipeline-test";

    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Cell:Id"] = CellId,
            ["Cell:Role"] = "app",
            ["Authentication:Authority"] = Issuer,
            ["Database:ConnectionString"] = "Host=127.0.0.1;Port=1;Username=nobody;Password=none;Database=none;Timeout=1",
            ["Valkey:ConnectionString"] = "127.0.0.1:1,abortConnect=false,connectTimeout=100",
            ["Realtime:RevocationFailClosed"] = "false",
            ["Organization:InvitationKeys:CurrentVersion"] = "1",
            ["Organization:InvitationKeys:Keys:1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });
        var key = new RsaSecurityKey(_rsa) { KeyId = "pipeline-test-key" };
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer, SigningKeys = { key } });

        _app = CellApp.Build(builder, CellRole.App);
        await _app.StartAsync(CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _rsa.Dispose();
    }

    [Fact]
    public async Task Write_WithoutAToken_IsUnauthorizedBeforeTheFenceAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Write_SignedInWhileTheEpochIsUnconfirmed_IsRefusedWithTheStableCodeAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, Token());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("code").GetString()
            .Should().Be("cell.epoch_unconfirmed");
    }

    [Fact]
    public async Task Read_SignedInWhileTheEpochIsUnconfirmed_IsNotFencedAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, Token(), "/api/v1/unknown-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string? token, string path = "/api/v1/orgs/0190a000-0000-7000-8000-000000000001/channels")
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { name = "general" });
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private string Token()
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audiences.Cell(CellId),
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [KeycloakClaims.Subject] = Guid.CreateVersion7().ToString(),
                [KeycloakClaims.SessionId] = "session-1",
                [KeycloakClaims.AuthorizedParty] = KeycloakClients.Web,
                [KeycloakClaims.TokenType] = KeycloakClaims.AccessTokenType,
            },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "pipeline-test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }
}
