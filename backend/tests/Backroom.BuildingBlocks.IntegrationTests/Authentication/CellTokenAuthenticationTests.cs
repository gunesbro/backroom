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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Authentication;

namespace Backroom.BuildingBlocks.IntegrationTests.Authentication;

/// <summary>
/// The control plane's two token kinds: cell tokens reach only cell endpoints, person tokens only
/// person endpoints (docs/contracts/phase-1.md, "Stream and identity").
/// </summary>
[Trait("Category", "Privacy")]
public sealed class CellTokenAuthenticationTests : IAsyncLifetime
{
    private readonly TestTokens _tokens = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration[$"{AccessTokenOptions.SectionName}:Authority"] = TestTokens.Issuer;
        builder.AddAccessTokenAuthentication(Audiences.ControlPlane, [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations]);
        builder.AddCellTokenAuthentication();
        foreach (var scheme in new[] { JwtBearerDefaults.AuthenticationScheme, AccessTokenAuthenticationExtensions.CellScheme })
        {
            builder.Services.Configure<JwtBearerOptions>(scheme, options =>
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer, SigningKeys = { _tokens.SigningKey } });
        }

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGet("/cell", (HttpContext context) => CellClients.TryGetCellId(context.User, out var cellId) ? cellId : "none")
            .RequireAuthorization(AuthorizationPolicies.Cell);
        _app.MapGet("/cell-account", (ICurrentAccount account) => account.AccountId.ToString()).RequireAuthorization(AuthorizationPolicies.Cell);
        _app.MapGet("/me", (ICurrentAccount account) => account.AccountId.ToString());
        _app.MapGet("/ops", () => "ops").RequireAuthorization(AuthorizationPolicies.Operations);
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
    public async Task CellEndpoint_WithTheCellsToken_IdentifiesTheCellAsync()
    {
        using var response = await GetAsync("/cell", CellToken("a"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Be("a");
    }

    public static TheoryData<string> RejectedCellTokens => new()
    {
        "person token",
        "with a session",
        "invalid cell id",
        "empty cell id",
        "admin client",
        "several audiences",
        "audience of a cell",
        "refresh token",
        "no client",
    };

    [Theory]
    [MemberData(nameof(RejectedCellTokens))]
    public async Task CellEndpoint_WithRejectedToken_IsUnauthorizedAsync(string kind)
    {
        var token = kind switch
        {
            "person token" => _tokens.Create(Audiences.ControlPlane),
            "with a session" => _tokens.Create(Audiences.ControlPlane, client: "cell-a", withSession: true),
            "invalid cell id" => CellToken("A"),
            "empty cell id" => _tokens.Create(Audiences.ControlPlane, client: "cell-", withSession: false),
            "admin client" => _tokens.Create(Audiences.ControlPlane, client: "control-plane-admin", withSession: false),
            "several audiences" => _tokens.Create(Audiences.ControlPlane, client: "cell-a", withSession: false, otherAudiences: [Audiences.Cell("b")]),
            "audience of a cell" => _tokens.Create(Audiences.Cell("a"), client: "cell-a", withSession: false),
            "refresh token" => _tokens.Create(Audiences.ControlPlane, client: "cell-a", withSession: false, tokenType: "Refresh"),
            _ => _tokens.Create(Audiences.ControlPlane, client: null, withSession: false),
        };

        using var response = await GetAsync("/cell", token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/me")]
    [InlineData("/ops")]
    public async Task PersonEndpoints_WithACellToken_AreUnauthorizedAsync(string path)
    {
        using var response = await GetAsync(path, CellToken("a"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CurrentAccount_OnACellRequest_IsNoAccountAsync()
    {
        // Regression: a cell's service account (GUID subject, no session) passed as a person's account.
        var read = () => GetAsync("/cell-account", CellToken("a"));

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("No account is signed in*");
    }

    private string CellToken(string cellId) => _tokens.Create(Audiences.ControlPlane, client: "cell-" + cellId, withSession: false);

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, CancellationToken);
    }
}
