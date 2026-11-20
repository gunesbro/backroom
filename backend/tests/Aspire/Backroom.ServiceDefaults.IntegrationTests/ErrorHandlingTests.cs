using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Errors;

namespace Backroom.ServiceDefaults.IntegrationTests;

/// <summary>
/// Error responses of a host built with the service defaults, in the Production environment.
/// </summary>
public sealed class ErrorHandlingTests : IClassFixture<ErrorHandlingTests.ProductionHost>
{
    private const string SecretMessage = "connection string Host=db;Password=hunter2";

    private readonly HttpClient _client;

    public ErrorHandlingTests(ProductionHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _client = host.Client;
    }

    [Fact]
    public async Task UnhandledException_WhenThrown_ReturnsProblemWithoutExceptionDetailsAsync()
    {
        using var response = await _client.GetAsync(new Uri("/throw", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().NotContain("hunter2").And.NotContain(nameof(InvalidOperationException)).And.NotContain("at ");
        AssertProblem(body, "server.unexpected");
    }

    [Theory]
    [InlineData("GET", "/missing", HttpStatusCode.NotFound, "http.not_found")]
    [InlineData("POST", "/throw", HttpStatusCode.MethodNotAllowed, "http.method_not_allowed")]
    public async Task BareStatusCode_WhenReturned_IsAProblemWithCodeAsync(string method, string path, HttpStatusCode status, string code)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        AssertProblem(body, code);
    }

    [Fact]
    public async Task DomainError_WhenReturned_KeepsItsCodeAndGetsTraceIdAsync()
    {
        using var response = await _client.GetAsync(new Uri("/conflict", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertProblem(body, "organization.already_member");
    }

    [Fact]
    public async Task RateLimitedError_WhenReturned_HasRetryAfterAsync()
    {
        using var response = await _client.GetAsync(new Uri("/rate-limited", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(30));
        AssertProblem(body, "directory.too_many_attempts");
    }

    private static void AssertProblem(string body, string code)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty(ProblemDetailsDefaults.CodeExtension).GetString().Should().Be(code);
        root.GetProperty(ProblemDetailsDefaults.TraceIdExtension).GetString().Should().NotBeNullOrWhiteSpace();
    }

    public sealed class ProductionHost : IAsyncLifetime
    {
        private WebApplication? _app;

        public HttpClient Client { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.AddServiceDefaults();

            var app = builder.Build();
            app.UseDefaultErrorHandling();
            app.MapGet("/throw", IResult () => throw new InvalidOperationException(SecretMessage));
            app.MapGet("/conflict", () => Error.Conflict("organization.already_member", "Already a member.").ToProblem());
            app.MapGet("/rate-limited", () =>
                Error.RateLimited("directory.too_many_attempts", "Try again later.", TimeSpan.FromSeconds(30)).ToProblem());
            app.MapDefaultEndpoints();

            await app.StartAsync();
            _app = app;
            Client = app.GetTestClient();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
