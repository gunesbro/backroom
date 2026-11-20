using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Authentication;

namespace Backroom.BuildingBlocks.IntegrationTests.Authentication;

/// <summary>Token validation of a cell host (audience <c>cell-a</c>) against forged and misdirected tokens.</summary>
[Trait("Category", "Privacy")]
public sealed class AccessTokenAuthenticationTests : IAsyncLifetime
{
    private static readonly string s_cellAudience = Audiences.Cell("a");

    private readonly TestTokens _tokens = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = CreateApp(Environments.Production, TestTokens.Issuer, _tokens.SigningKey);
        await _app.StartAsync(CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _tokens.Dispose();
    }

    [Fact]
    public async Task Request_WithValidAccessToken_IdentifiesTheAccountAsync()
    {
        var accountId = Guid.CreateVersion7();

        using var response = await GetAsync("/me", _tokens.Create(s_cellAudience, accountId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Be(accountId.ToString());
    }

    [Fact]
    public async Task Request_FromTheDesktopApp_IsAcceptedAsync()
    {
        using var response = await GetAsync("/me", _tokens.Create(s_cellAudience, client: KeycloakClients.Desktop));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Request_WithoutToken_IsUnauthorizedAsync()
    {
        using var response = await GetAsync("/me", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
    }

    [Fact]
    public async Task HealthEndpoint_WithoutToken_IsAllowedAsync()
    {
        using var response = await GetAsync("/health", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string> RejectedTokens => new()
    {
        "audience of another cell",
        "audience of the control plane",
        "expired",
        "other issuer",
        "signed by another key",
        "unsigned",
        "symmetric algorithm",
        "id token",
        "refresh token",
        "no token type",
        "subject is not an account id",
        "also for the control plane",
        "also for another cell",
        "service account without session",
        "client not accepted here",
        "no client",
        "not valid yet",
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Request_WithRejectedToken_IsUnauthorizedAsync(string kind)
    {
        var token = kind switch
        {
            "audience of another cell" => _tokens.Create(Audiences.Cell("b")),
            "audience of the control plane" => _tokens.Create(Audiences.ControlPlane),
            "expired" => _tokens.Create(s_cellAudience, expires: DateTime.UtcNow.AddMinutes(-2)),
            "other issuer" => _tokens.Create(s_cellAudience, issuer: "https://id.example.test/realms/other"),
            "signed by another key" => _tokens.Create(s_cellAudience, key: _tokens.OtherKey),
            "unsigned" => TestTokens.CreateUnsigned(s_cellAudience),
            "symmetric algorithm" => _tokens.Create(
                s_cellAudience,
                key: new SymmetricSecurityKey(new byte[32]) { KeyId = "test-key" },
                algorithm: SecurityAlgorithms.HmacSha256),
            "id token" => _tokens.Create(s_cellAudience, tokenType: "ID"),
            "refresh token" => _tokens.Create(s_cellAudience, tokenType: "Refresh"),
            "no token type" => _tokens.Create(s_cellAudience, tokenType: null),
            "subject is not an account id" => _tokens.Create(s_cellAudience, subject: "service-account-name"),

            // Regression: a token naming several targets was accepted by each of them.
            "also for the control plane" => _tokens.Create(s_cellAudience, otherAudiences: [Audiences.ControlPlane]),
            "also for another cell" => _tokens.Create(s_cellAudience, otherAudiences: [Audiences.Cell("b")]),

            // Regression: what Keycloak issues for client credentials (GUID subject, no session).
            "service account without session" => _tokens.Create(s_cellAudience, client: "cell-a", withSession: false),
            "client not accepted here" => _tokens.Create(s_cellAudience, client: "cell-a"),
            "no client" => _tokens.Create(s_cellAudience, client: null),
            _ => _tokens.Create(s_cellAudience, notBefore: DateTime.UtcNow.AddMinutes(2), expires: DateTime.UtcNow.AddMinutes(7)),
        };

        using var response = await GetAsync("/me", token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("ops", true, true, HttpStatusCode.OK)]
    [InlineData("ops", true, false, HttpStatusCode.Forbidden)]
    [InlineData("ops", false, true, HttpStatusCode.Forbidden)]
    [InlineData("ops", false, false, HttpStatusCode.Forbidden)]
    [InlineData("web", true, true, HttpStatusCode.Forbidden)]
    [InlineData("desktop", true, true, HttpStatusCode.Forbidden)]
    public async Task OperationsPolicy_RequiresOpsClientRoleAndOneTimePasswordAsync(string client, bool opsRole, bool otp, HttpStatusCode expected)
    {
        var token = _tokens.Create(
            s_cellAudience,
            client: client,
            realmRoles: opsRole ? ["ops", "default-roles-backroom"] : ["default-roles-backroom"],
            authenticationMethods: otp ? ["pwd", "otp"] : ["pwd"]);

        using var response = await GetAsync("/ops", token);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Startup_WithHttpAuthorityOutsideDevelopment_FailsAsync()
    {
        await using var app = CreateApp(Environments.Production, "http://id.example.test/realms/backroom", _tokens.SigningKey);

        var start = () => app.StartAsync(CancellationToken);

        await start.Should().ThrowAsync<OptionsValidationException>().WithMessage("*https*");
    }

    [Fact]
    public async Task Startup_WithHttpAuthorityInDevelopment_SucceedsAsync()
    {
        await using var app = CreateApp(Environments.Development, "http://localhost:8080/realms/backroom", _tokens.SigningKey);

        await app.StartAsync(CancellationToken);
        await app.StopAsync(CancellationToken);
    }

    /// <summary>
    /// A host's authentication. With <paramref name="signingKey"/>, the realm's signing keys are given
    /// directly instead of downloaded, so the test needs no network.
    /// </summary>
    internal static WebApplication CreateApp(string environment, string authority, SecurityKey? signingKey, string? audience = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration[$"{AccessTokenOptions.SectionName}:Authority"] = authority;
        builder.AddAccessTokenAuthentication(audience ?? s_cellAudience, [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations]);
        if (signingKey is not null)
        {
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Configuration = new OpenIdConnectConfiguration { Issuer = authority, SigningKeys = { signingKey } });
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/me", (ICurrentAccount account) => account.AccountId.ToString());
        app.MapGet("/ops", () => "ops").RequireAuthorization(AuthorizationPolicies.Operations);
        app.MapGet("/health", () => "ok").AllowAnonymous();
        return app;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, CancellationToken);
    }
}
