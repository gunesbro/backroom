using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Cors;

namespace Backroom.BuildingBlocks.IntegrationTests.Cors;

/// <summary>The CORS policy of the hosts: configured origins only, bearer tokens only, answered before authentication.</summary>
public sealed class WebOriginsCorsTests
{
    private const string Allowed = "http://localhost:5173";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preflight_FromAllowedOrigin_IsAnsweredWithoutTokenAndCredentialsAsync()
    {
        await using var app = await StartAsync(Allowed, "https://app.example.test");

        using var response = await SendAsync(app, HttpMethod.Options, "/me", Allowed, preflightMethod: "GET", preflightHeaders: "authorization");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(Allowed);
        response.Headers.GetValues("Access-Control-Allow-Methods").Single().Should().Contain("GET").And.Contain("PUT").And.Contain("PATCH").And.Contain("DELETE");
        response.Headers.GetValues("Vary").Should().Contain("Origin");
        response.Headers.GetValues("Access-Control-Allow-Headers").Single().Should().ContainEquivalentOf("authorization");
        response.Headers.GetValues("Access-Control-Max-Age").Should().ContainSingle().Which.Should().Be("600");
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task Preflight_FromOtherOrigin_GetsNoCorsHeadersAsync()
    {
        await using var app = await StartAsync(Allowed);

        using var response = await SendAsync(app, HttpMethod.Options, "/me", "https://evil.example.test", preflightMethod: "GET", preflightHeaders: "authorization");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Vary").Should().Contain("Origin");
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Methods").Should().BeFalse();
    }

    [Fact]
    public async Task Request_FromOtherOrigin_GetsNoCorsHeadersAsync()
    {
        await using var app = await StartAsync(Allowed);

        using var response = await SendAsync(app, HttpMethod.Get, "/open", "https://evil.example.test");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Request_WithEmptyOriginList_GetsNoCorsHeadersAsync()
    {
        await using var app = await StartAsync();

        using var response = await SendAsync(app, HttpMethod.Get, "/open", Allowed);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Request_FromAllowedOrigin_CarriesAllowOriginAndExposedHeadersAsync()
    {
        await using var app = await StartAsync(Allowed);

        using var response = await SendAsync(app, HttpMethod.Get, "/open", Allowed);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(Allowed);
        response.Headers.GetValues("Access-Control-Expose-Headers").Single().Should().Be("Retry-After");
        response.Headers.GetValues("Vary").Should().Contain("Origin");
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Theory]
    [InlineData("OPTIONS", "http://localhost:5173", true)]
    [InlineData("OPTIONS", "http://localhost:5173", false)]
    [InlineData("OPTIONS", "https://evil.example.test", true)]
    [InlineData("OPTIONS", "https://evil.example.test", false)]
    [InlineData("GET", "http://localhost:5173", true)]
    [InlineData("GET", "http://localhost:5173", false)]
    [InlineData("GET", "https://evil.example.test", true)]
    [InlineData("GET", "https://evil.example.test", false)]
    public async Task Response_PassingCors_HasExactlyOneVaryOriginAsync(string method, string origin, bool severalOrigins)
    {
        // With several configured origins the CORS middleware adds Vary: Origin itself; the host must not add a second one.
        await using var app = severalOrigins ? await StartAsync(Allowed, "https://app.example.test") : await StartAsync(Allowed);
        var preflight = method == "OPTIONS";

        using var response = await SendAsync(
            app, new HttpMethod(method), "/open", origin, preflightMethod: preflight ? "GET" : null, preflightHeaders: preflight ? "authorization" : null);

        var tokens = response.Headers.GetValues("Vary").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries));
        tokens.Count(t => string.Equals(t, "Origin", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task Request_FromAllowedOriginWithoutToken_IsStillUnauthorizedAsync()
    {
        await using var app = await StartAsync(Allowed);

        using var response = await SendAsync(app, HttpMethod.Get, "/me", Allowed);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_WithNullOrigin_GetsNoCorsHeadersAsync()
    {
        await using var app = await StartAsync(Allowed);

        using var response = await SendAsync(app, HttpMethod.Get, "/open", "null");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Request_FromTheDesktopReleaseOrigin_IsAllowedInProductionAsync()
    {
        await using var app = await StartInAsync(Environments.Production, "http://tauri.localhost", "https://app.example.test");

        using var response = await SendAsync(app, HttpMethod.Get, "/open", "http://tauri.localhost");

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("http://tauri.localhost");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.test")]
    [InlineData("https://app.example.test/")]
    [InlineData("https://app.example.test/path")]
    [InlineData("https://user@app.example.test")]
    [InlineData("http://app.example.test")]
    [InlineData("ftp://app.example.test")]
    [InlineData("app.example.test")]
    [InlineData("https://x:443")]
    [InlineData("HTTPS://App.Example.test")]
    [InlineData("https://App.example.test")]
    [InlineData("https://bücher.example")]
    [InlineData("null")]
    [InlineData("http://foo.localhost")]
    [InlineData("http://localhost:5173", "Production")]
    [InlineData("http://127.0.0.1:5173", "Production")]
    public async Task Startup_WithInvalidOrigin_FailsAsync(string origin, string environment = "Development")
    {
        var start = async () => await StartInAsync(environment, origin);

        await start.Should().ThrowAsync<OptionsValidationException>();
    }

    [Theory]
    [InlineData("https://app.example.test")]
    [InlineData("https://app.example.test:8443")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://tauri.localhost")]
    [InlineData("https://xn--bcher-kva.example")]
    [InlineData("tauri://localhost")]
    public async Task Startup_WithExactOrigin_SucceedsAsync(string origin)
    {
        await using var app = await StartAsync(origin);

        app.Should().NotBeNull();
    }

    private static Task<WebApplication> StartAsync(params string[] origins) => StartInAsync(Environments.Development, origins);

    private static async Task<WebApplication> StartInAsync(string environment, params string[] origins)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration[$"{AccessTokenOptions.SectionName}:Authority"] = "https://id.example.test/realms/backroom";
        for (var i = 0; i < origins.Length; i++)
        {
            builder.Configuration[$"{WebOriginsOptions.SectionName}:AllowedOrigins:{i}"] = origins[i];
        }

        builder.AddAccessTokenAuthentication(Audiences.Cell("a"), [KeycloakClients.Web]);
        builder.AddWebOriginsCors();

        var app = builder.Build();
        app.UseWebOriginsCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/me", () => "me");
        app.MapGet("/open", () => "open").AllowAnonymous();
        try
        {
            await app.StartAsync(CancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplication app, HttpMethod method, string path, string origin, string? preflightMethod = null, string? preflightHeaders = null)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Origin", origin);
        if (preflightMethod is not null)
        {
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", preflightMethod);
        }

        if (preflightHeaders is not null)
        {
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", preflightHeaders);
        }

        return await client.SendAsync(request, CancellationToken);
    }
}
