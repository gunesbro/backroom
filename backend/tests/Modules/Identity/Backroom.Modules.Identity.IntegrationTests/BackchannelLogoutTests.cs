using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Identity.Application;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Backroom.Modules.Identity.Contracts;
using Backroom.Modules.Identity.Presentation;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>
/// Keycloak's back-channel logout endpoint (OIDC Back-Channel Logout 1.0, docs/contracts/phase-1.md, flow 5): what it accepts, what
/// it stores and sends to the cells, and that every kind of invalid token is refused without a trace. The test host trusts a key
/// generated for the run as the realm's key and runs on a fake clock in 2003 (other classes use other years).
/// </summary>
[Trait("Category", "Privacy")]
public sealed class BackchannelLogoutTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2003, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(s_now);
    private readonly LogoutTokenIssuer _issuer = new();
    private readonly TestData _data = new(database);
    private WebApplication _controlPlane = null!;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await IdentityUnderTest.StartAsync(database, CancellationToken, _time, _issuer.TrustedKey);
        _client = _controlPlane.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _controlPlane.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact]
    public async Task Logout_WithAValidToken_RecordsTheSessionAndAnswers200WithoutCachingAsync()
    {
        var account = Guid.NewGuid();
        var session = Guid.NewGuid().ToString("D");

        using var response = await PostAsync(_issuer.Create(_time.GetUtcNow(), account, session));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the specification requires the answer not to be cached");
        var row = await _data.RevokedSessionAsync(account);
        row.SessionId.Should().Be(session);
        new DateTimeOffset(DateTime.SpecifyKind(row.RevokedAt, DateTimeKind.Utc)).Should().Be(s_now);
        new DateTimeOffset(DateTime.SpecifyKind(row.KeepUntil, DateTimeKind.Utc)).Should().Be(
            s_now + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30),
            "a revocation is kept for the longest access token lifetime (5 minutes) plus the clock skew (30 seconds)");
    }

    [Fact]
    public async Task Logout_WithAnAudienceOfAnotherPeopleClient_IsAcceptedAsync()
    {
        foreach (var client in new[] { "web", "desktop", "ops" })
        {
            var account = Guid.NewGuid();

            using var response = await PostAsync(_issuer.Create(_time.GetUtcNow(), account, Guid.NewGuid().ToString("D"), payload => payload["aud"] = client));

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"the {client} client is one of ours");
        }
    }

    [Fact]
    public async Task Logout_WithTheFreshnessLimitOfIat_IsAcceptedAsync()
    {
        // Just inside the two minutes, and just inside the skew into the future.
        using var older = await PostAsync(_issuer.Create(_time.GetUtcNow() - LogoutTokenValidator.MaxAge + TimeSpan.FromSeconds(1), Guid.NewGuid(), "s-old"));
        using var newer = await PostAsync(_issuer.Create(_time.GetUtcNow() + TimeSpan.FromSeconds(29), Guid.NewGuid(), "s-new"));

        older.StatusCode.Should().Be(HttpStatusCode.OK);
        newer.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_RepeatedForTheSameSession_StoresOneRowAndSendsOneMessagePerCellAsync()
    {
        var account = Guid.NewGuid();
        var session = Guid.NewGuid().ToString("D");
        var cell = await _data.AddCellAsync();
        await _data.AddMembershipAsync(account, cell);
        var token = _issuer.Create(_time.GetUtcNow(), account, session);

        using var first = await PostAsync(token);
        using var second = await PostAsync(_issuer.Create(_time.GetUtcNow(), account, session));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "a duplicated delivery is not an error");
        (await _data.CountRevokedSessionsAsync(account)).Should().Be(1);
        (await _data.SessionRevokedMessagesAsync(cell)).Should().ContainSingle("a repeated logout must not be sent to the cell again");
    }

    [Fact]
    public async Task Logout_ForAnAccountWithMembershipsInTwoCells_TellsThoseCellsOnlyAsync()
    {
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        var (first, second, third, failed) = (await _data.AddCellAsync(), await _data.AddCellAsync(), await _data.AddCellAsync(), await _data.AddCellAsync());
        await _data.AddMembershipAsync(account, first);
        await _data.AddMembershipAsync(account, first); // a second organization in the same cell: one message
        await _data.AddMembershipAsync(account, second, status: "pending");
        await _data.AddMembershipAsync(account, third, status: "removed");
        await _data.AddMembershipAsync(account, failed, organizationStatus: "failed");
        var unrelated = await _data.AddCellAsync();
        await _data.AddMembershipAsync(other, unrelated);
        var session = Guid.NewGuid().ToString("D");

        using var response = await PostAsync(_issuer.Create(_time.GetUtcNow(), account, session));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var toFirst = (await _data.SessionRevokedMessagesAsync(first)).Should().ContainSingle().Subject;
        toFirst.AccountId.Should().Be(account.ToString("D"));
        toFirst.SessionId.Should().Be(session);
        toFirst.RevokedAt.ToDateTimeOffset().Should().Be(s_now);
        toFirst.KeepUntil.ToDateTimeOffset().Should().Be(s_now + SessionRevocationPolicy.KeepFor);
        (await _data.SessionRevokedMessagesAsync(second)).Should().ContainSingle("a membership that is still pending counts");
        (await _data.SessionRevokedMessagesAsync(third)).Should().BeEmpty("the person was removed from that organization");
        (await _data.SessionRevokedMessagesAsync(failed)).Should().BeEmpty("the organization never came to exist");
        (await _data.SessionRevokedMessagesAsync(unrelated)).Should().BeEmpty("it is another person's cell");
    }

    [Fact]
    public async Task Logout_ForAnAccountWithoutMemberships_StoresTheRevocationAndSendsNothingAsync()
    {
        var account = Guid.NewGuid();

        using var response = await PostAsync(_issuer.Create(_time.GetUtcNow(), account, "s-lonely"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _data.CountRevokedSessionsAsync(account)).Should().Be(1, "FullSync reports it if a membership appears");
    }

    [Fact]
    public async Task Revoke_ForEverySessionTwice_KeepsOneRowAndTellsTheCellAgainAsync()
    {
        // The endpoint always has a session id; revoking every session is Identity's own operation (for later callers).
        var account = Guid.NewGuid();
        var cell = await _data.AddCellAsync();
        await _data.AddMembershipAsync(account, cell);

        await RevokeEverySessionAsync(account);
        _time.Advance(TimeSpan.FromSeconds(10));
        await RevokeEverySessionAsync(account);

        (await _data.CountRevokedSessionsAsync(account)).Should().Be(1);
        var messages = await _data.SessionRevokedMessagesAsync(cell);
        messages.Should().HaveCount(2);
        messages[1].RevokedAt.ToDateTimeOffset().Should().Be(s_now.AddSeconds(10), "the later revocation moves it forward");
        messages.Should().OnlyContain(message => message.SessionId.Length == 0);
    }

    private async Task RevokeEverySessionAsync(Guid account)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SessionRevocations>().RevokeAsync(account, null, CancellationToken);
    }

    public static TheoryData<string> InvalidTokens() => new()
    {
        "signature",
        "unsigned",
        "symmetric",
        "issuer",
        "audience",
        "no audience",
        "other audience",
        "expired",
        "stale iat",
        "future iat",
        "no iat",
        "no events",
        "other event",
        "events not an object",
        "event not an object",
        "nonce",
        "no jti",
        "no subject",
        "subject not an id",
        "empty subject id",
        "oversized sid",
        "sid with a space",
        "sid not a string",
        "access token",
        "no sid",
    };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Logout_WithAnInvalidToken_IsRefusedAndLeavesNoTraceAsync(string kind)
    {
        var account = Guid.NewGuid();
        var cell = await _data.AddCellAsync();
        await _data.AddMembershipAsync(account, cell);
        var session = Guid.NewGuid().ToString("D");
        var now = _time.GetUtcNow();
        var token = kind switch
        {
            "signature" => _issuer.Create(now, account, session, key: _issuer.UntrustedKey),
            "unsigned" => _issuer.CreateUnsigned(now, account),
            "symmetric" => _issuer.Create(now, account, session, key: new SymmetricSecurityKey(new byte[64]), algorithm: SecurityAlgorithms.HmacSha256),
            "issuer" => _issuer.Create(now, account, session, payload => payload["iss"] = "https://evil.example.test/realms/backroom"),
            "audience" => _issuer.Create(now, account, session, payload => payload["aud"] = "control-plane"),
            "no audience" => _issuer.Create(now, account, session, payload => payload.Remove("aud")),
            "other audience" => _issuer.Create(now, account, session, payload => payload["aud"] = "cell-a"),
            "expired" => _issuer.Create(now, account, session, payload => payload["exp"] = now.AddMinutes(-5).ToUnixTimeSeconds()),
            "stale iat" => _issuer.Create(now - LogoutTokenValidator.MaxAge - TimeSpan.FromSeconds(5), account, session),
            "future iat" => _issuer.Create(now + TimeSpan.FromMinutes(5), account, session),
            "no iat" => _issuer.Create(now, account, session, payload => payload.Remove("iat")),
            "no events" => _issuer.Create(now, account, session, payload => payload.Remove("events")),
            "other event" => _issuer.Create(now, account, session, payload => payload["events"] = new JsonObject { ["http://schemas.openid.net/event/other"] = new JsonObject() }),
            "events not an object" => _issuer.Create(now, account, session, payload => payload["events"] = "backchannel-logout"),
            "event not an object" => _issuer.Create(now, account, session, payload => payload["events"] = new JsonObject { [LogoutTokenIssuer.EventName] = true }),
            "nonce" => _issuer.Create(now, account, session, payload => payload["nonce"] = "n-0S6_WzA2Mj"),
            "no jti" => _issuer.Create(now, account, session, payload => payload.Remove("jti")),
            "no subject" => _issuer.Create(now, account, session, payload => payload.Remove("sub")),
            "subject not an id" => _issuer.Create(now, account, session, payload => payload["sub"] = "alice"),
            "empty subject id" => _issuer.Create(now, account, session, payload => payload["sub"] = Guid.Empty.ToString("D")),
            "oversized sid" => _issuer.Create(now, account, new string('s', 129)),
            "sid with a space" => _issuer.Create(now, account, "session one"),
            "sid not a string" => _issuer.Create(now, account, session, payload => payload["sid"] = 42),
            "no sid" => _issuer.Create(now, account, session, payload => payload.Remove("sid")),
            "access token" => _issuer.Create(now, account, session, payload =>
            {
                payload["typ"] = "Bearer";
                payload["azp"] = "web";
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var response = await PostAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;
        body["error"]!.GetValue<string>().Should().Be("invalid_request");
        body["error_description"]!.GetValue<string>().Should().NotContain(session, "the answer says nothing about what was wrong");
        (await _data.CountRevokedSessionsAsync(account)).Should().Be(0);
        (await _data.SessionRevokedMessagesAsync(cell)).Should().BeEmpty();
    }

    [Fact]
    public async Task Logout_WithoutAFormToken_IsRefusedAsync()
    {
        using var jsonBody = await _client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative),
            new StringContent("""{"logout_token":"x"}""", Encoding.UTF8, "application/json"),
            CancellationToken);
        using var noField = await _client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative), new FormUrlEncodedContent([new("other", "x")]), CancellationToken);
        var token = _issuer.Create(_time.GetUtcNow(), Guid.NewGuid(), "s-twice");
        using var twoFields = await _client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative),
            new FormUrlEncodedContent([new("logout_token", token), new("logout_token", token)]),
            CancellationToken);
        using var inQuery = await _client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath + "?logout_token=" + token, UriKind.Relative), new FormUrlEncodedContent([]), CancellationToken);
        using var oversized = await _client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative),
            new FormUrlEncodedContent([new("logout_token", new string('a', LogoutTokenValidator.MaxTokenLength + 1))]),
            CancellationToken);

        jsonBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noField.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        twoFields.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        inQuery.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        oversized.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Logout_DoesNotNeedABearerTokenAsync()
    {
        // The logout token authenticates the call; the request carries no Authorization header.
        using var response = await PostAsync(_issuer.Create(_time.GetUtcNow(), Guid.NewGuid(), "s-anonymous"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().NotContain(header => header.Key == "WWW-Authenticate");
    }

    private Task<HttpResponseMessage> PostAsync(string logoutToken) =>
        _client.PostAsync(
            new Uri(IdentityEndpoints.BackchannelLogoutPath, UriKind.Relative),
            new FormUrlEncodedContent([new("logout_token", logoutToken)]),
            CancellationToken);
}
