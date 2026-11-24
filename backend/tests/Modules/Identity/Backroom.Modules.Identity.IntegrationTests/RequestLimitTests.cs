using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Identity.Presentation;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>
/// The request size limit of the logout endpoint (16 KB). The test server cannot apply a body limit, so these tests run the control
/// plane on Kestrel, on a free local port.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class RequestLimitTests(PostgresDatabase database)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logout_WithABodyOver16KB_IsRefusedBeforeItIsReadAsync()
    {
        using var issuer = new LogoutTokenIssuer();
        await using var app = await IdentityUnderTest.StartAsync(
            database, CancellationToken, new FakeTimeProvider(DateTimeOffset.UtcNow), issuer.TrustedKey, listenOn: "http://127.0.0.1:0");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var oversized = await client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative),
            new FormUrlEncodedContent([new("logout_token", new string('a', 16 * 1024 + 1))]),
            CancellationToken);
        using var small = await client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative),
            new FormUrlEncodedContent([new("logout_token", "not-a-token")]),
            CancellationToken);

        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        small.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a small body is read and the token refused");
    }
}
