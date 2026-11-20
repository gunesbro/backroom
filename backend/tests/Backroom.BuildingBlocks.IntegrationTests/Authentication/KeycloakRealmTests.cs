using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Testing.Keycloak;

namespace Backroom.BuildingBlocks.IntegrationTests.Authentication;

/// <summary>The development realm in a real Keycloak, and its real tokens validated by a host's authentication.</summary>
[Trait("Category", "Privacy")]
public sealed class KeycloakRealmTests(KeycloakRealm realm) : IClassFixture<KeycloakRealm>
{
    private const string DesktopCallback = "http://127.0.0.1/callback";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("web")]
    [InlineData("desktop")]
    [InlineData("ops")]
    public void Client_WhenImported_HasSafeSettings(string clientId)
    {
        var client = realm.ImportedClients[clientId];

        client["publicClient"]!.GetValue<bool>().Should().BeTrue();
        client["directAccessGrantsEnabled"]!.GetValue<bool>().Should().BeFalse("passwords are entered only on Keycloak's pages");
        client["implicitFlowEnabled"]!.GetValue<bool>().Should().BeFalse();
        client["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>().Should().Be("S256");
        client["redirectUris"]!.AsArray().Select(uri => uri!.GetValue<string>())
            .Should().OnlyContain(uri => uri.StartsWith("http://localhost:", StringComparison.Ordinal) || uri == DesktopCallback);
    }

    [Fact]
    public void Desktop_WhenImported_RedirectsOnlyToTheLoopbackCallbackAndHasNoWebOrigins()
    {
        // Contract, flow 1 (desktop): exactly this redirect URI, no wildcard, no localhost; the app, not a web page,
        // requests tokens, so Keycloak allows no cross-origin calls for this client.
        var desktop = realm.ImportedClients["desktop"];

        desktop["redirectUris"]!.AsArray().Select(uri => uri!.GetValue<string>()).Should().Equal(DesktopCallback);
        desktop["webOrigins"]!.AsArray().Should().BeEmpty();
        desktop["fullScopeAllowed"]!.GetValue<bool>().Should().BeFalse();
        desktop["attributes"]!["exclude.issuer.from.auth.response"]!.GetValue<string>().Should().Be("false", "the app checks iss (RFC 9207)");
    }

    [Theory]
    [InlineData("http://127.0.0.1:49152/callback", HttpStatusCode.OK)]
    [InlineData("http://127.0.0.1:65535/callback", HttpStatusCode.OK)]
    [InlineData("http://127.0.0.1:49152/other", HttpStatusCode.BadRequest)]
    [InlineData("http://127.0.0.1:49152/callback/more", HttpStatusCode.BadRequest)]
    [InlineData("http://127.0.0.1:49152/callback?next=x", HttpStatusCode.BadRequest)]
    [InlineData("http://127.0.0.1:49152/callback#x", HttpStatusCode.BadRequest)]
    [InlineData("http://[::1]:49152/callback", HttpStatusCode.BadRequest)]
    [InlineData("http://localhost:49152/callback", HttpStatusCode.BadRequest)]
    [InlineData("http://127.0.0.1.example.test:49152/callback", HttpStatusCode.BadRequest)]
    [InlineData("https://example.test/callback", HttpStatusCode.BadRequest)]
    public async Task DesktopSignIn_WithARedirectUri_AcceptsAnyLoopbackPortOnTheCallbackOnlyAsync(string redirectUri, HttpStatusCode expected)
    {
        // RFC 8252 §7.3: the app listens on a port the operating system picks, so any port must work, nothing else.
        var query = "client_id=desktop&response_type=code&scope=openid&state=test-state"
            + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256"
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri);

        (await realm.GetStatusAsync("/protocol/openid-connect/auth?" + query)).Should().Be(expected);
    }

    [Fact]
    public void Realm_WhenImported_HasShortTokensBruteForceProtectionAndFilledPlaceholders()
    {
        realm.ImportedRealm["accessTokenLifespan"]!.GetValue<int>().Should().Be(300);
        realm.ImportedRealm["bruteForceProtected"]!.GetValue<bool>().Should().BeTrue();
        realm.ImportedRealm["smtpServer"]!["host"]!.GetValue<string>().Should().Be("localhost", "the placeholder is filled from the environment");
    }

    [Fact]
    public void Realm_WhenImported_HasTheContractSessionLifetimes()
    {
        // Contract "Sessions": 30 minutes idle and 10 hours in total; with "Remember me" 30 days idle and 90 days.
        realm.ImportedRealm["rememberMe"]!.GetValue<bool>().Should().BeTrue();
        realm.ImportedRealm["ssoSessionIdleTimeout"]!.GetValue<int>().Should().Be(30 * 60);
        realm.ImportedRealm["ssoSessionMaxLifespan"]!.GetValue<int>().Should().Be(10 * 60 * 60);
        realm.ImportedRealm["ssoSessionIdleTimeoutRememberMe"]!.GetValue<int>().Should().Be(30 * 24 * 60 * 60);
        realm.ImportedRealm["ssoSessionMaxLifespanRememberMe"]!.GetValue<int>().Should().Be(90 * 24 * 60 * 60);
        realm.ImportedRealm["revokeRefreshToken"]!.GetValue<bool>().Should().BeTrue("refresh tokens are single-use");
        realm.ImportedRealm["refreshTokenMaxReuse"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task VerifyEmail_WhenResending_WaitsAtLeast30SecondsBetweenEmailsAsync()
    {
        // "Send again" on the verify-email page could otherwise be used to flood someone's inbox. Keycloak enforces
        // this itself; the test notices if an upgrade or a realm setting lowers it.
        (await realm.VerificationEmailCooldownSecondsAsync()).Should().BeGreaterThanOrEqualTo(30);
    }

    [Fact]
    public async Task OfflineAccess_ForANewUser_IsNeitherGrantedNorIssuedAsync()
    {
        // Contract "No offline tokens": an offline token would outlive sign-out and the session limits.
        var username = $"new-{Guid.NewGuid():N}@example.test";
        await realm.CreateUserAsync(username);

        (await realm.EffectiveRealmRolesAsync(username)).Should().NotContain("offline_access");
        (await realm.DefaultOptionalClientScopesAsync()).Should().NotContain("offline_access", "new clients must not get the scope either");
        foreach (var (clientId, scopes) in await realm.ClientScopesAsync())
        {
            scopes.Should().NotContain("offline_access", $"client {clientId} must not offer offline tokens");
        }

        var (status, body) = await realm.PasswordGrantAsync(username, "openid offline_access");
        status.Should().Be(HttpStatusCode.BadRequest);
        body["error"]!.GetValue<string>().Should().Be("invalid_scope", "no client has the scope");

        // The second layer: even a client that offered the scope gets no offline token, because the user lacks the role.
        await using (await realm.AddOptionalClientScopeAsync("web", "offline_access"))
        {
            var (offlineStatus, offlineBody) = await realm.PasswordGrantAsync(username, "openid offline_access");
            offlineStatus.Should().Be(HttpStatusCode.BadRequest);
            offlineBody["error"]!.GetValue<string>().Should().Be("not_allowed", "offline tokens need the offline_access role");
        }

        var (onlineStatus, onlineBody) = await realm.PasswordGrantAsync(username, "openid");
        onlineStatus.Should().Be(HttpStatusCode.OK);
        RefreshTokenType(onlineBody).Should().Be("Refresh", "a normal sign-in gets a session-bound refresh token");
    }

    [Fact]
    public async Task Refresh_WithOneOfTheScopesGrantedAtSignIn_GetsATokenForThatTargetOnlyAsync()
    {
        // Contract "Audience per target": sign-in grants several targets, each token request names one.
        var cell = await realm.CreateCellScopeAsync();
        var username = $"new-{Guid.NewGuid():N}@example.test";
        await realm.CreateUserAsync(username);
        var (_, signIn) = await realm.PasswordGrantAsync(username, $"openid control-plane cell:{cell}");

        var (cellStatus, forCell) = await realm.RefreshAsync(signIn["refresh_token"]!.GetValue<string>(), $"cell:{cell}");
        var (controlPlaneStatus, forControlPlane) = await realm.RefreshAsync(forCell["refresh_token"]!.GetValue<string>(), "control-plane");
        var (againStatus, forCellAgain) = await realm.RefreshAsync(forControlPlane["refresh_token"]!.GetValue<string>(), $"cell:{cell}");

        AudiencesOf(signIn).Should().BeEquivalentTo(["control-plane", $"cell-{cell}"], "so the client discards the sign-in token unused");
        cellStatus.Should().Be(HttpStatusCode.OK);
        AudiencesOf(forCell).Should().Equal($"cell-{cell}");
        controlPlaneStatus.Should().Be(HttpStatusCode.OK);
        AudiencesOf(forControlPlane).Should().Equal("control-plane");
        againStatus.Should().Be(HttpStatusCode.OK);
        AudiencesOf(forCellAgain).Should().Equal([$"cell-{cell}"], "narrowing keeps the whole grant in the rotated refresh token");
    }

    [Fact]
    public async Task Refresh_WithAScopeNotGrantedAtSignIn_AddsNoAudienceEvenNextToGrantedOnesAsync()
    {
        // Why sign-in requests every target's scope: Keycloak keeps a refresh within the scopes granted at sign-in and
        // does not fall back to the whole grant. If this changes after an upgrade, revisit the contract's sign-in scopes.
        var granted = await realm.CreateCellScopeAsync();
        var notGranted = await realm.CreateCellScopeAsync();
        var username = $"new-{Guid.NewGuid():N}@example.test";
        await realm.CreateUserAsync(username);
        var (_, signIn) = await realm.PasswordGrantAsync(username, $"openid control-plane cell:{granted}");

        var (status, refreshed) = await realm.RefreshAsync(signIn["refresh_token"]!.GetValue<string>(), $"cell:{notGranted}");

        status.Should().Be(HttpStatusCode.OK);
        AudiencesOf(refreshed).Should().BeEmpty();
        refreshed["scope"]!.GetValue<string>().Should().NotContain($"cell:{notGranted}", "the client checks the answer's scope");
    }

    private static IEnumerable<string> AudiencesOf(JsonNode tokenResponse) =>
        new JsonWebToken(tokenResponse["access_token"]!.GetValue<string>()).Audiences;

    private static string RefreshTokenType(JsonNode tokenResponse) =>
        JsonNode.Parse(Base64Url.DecodeFromChars(tokenResponse["refresh_token"]!.GetValue<string>().Split('.')[1]))!["typ"]!.GetValue<string>();

    [Fact]
    public async Task Server_WhenStarted_HasNoAccountConsoleAccountApiOrImpersonationAsync()
    {
        // ADR 0009: account settings live in our app, and no one may act as another person (privacy rules).
        // The admin API names features in upper case: account is ACCOUNT_V3.
        var features = (await realm.ServerFeaturesAsync()).AsArray()
            .ToDictionary(feature => feature!["name"]!.GetValue<string>(), feature => feature!["enabled"]!.GetValue<bool>(), StringComparer.Ordinal);

        features.Should().Contain("ACCOUNT_V3", false);
        features.Should().Contain("ACCOUNT_API", false);
        features.Should().Contain("IMPERSONATION", false);
        features.Should().Contain("TOKEN_EXCHANGE", false, "token exchange v1 could also act as another user");
        (await realm.GetStatusAsync("/account")).Should().Be(HttpStatusCode.NotFound, "the account console is off");
    }

    [Fact]
    public async Task CopiedLoginTemplates_ComparedWithThisKeycloak_AreUnchangedUpstreamAsync()
    {
        // Our login theme copies keycloak.v2 templates (ADR 0009). When an upgrade changes one, merge Keycloak's change
        // into our copy, then record the new hash and version in upstream-templates.sha256.
        var hashes = Path.Combine(AppContext.BaseDirectory, "keycloak", "theme", "upstream-templates.sha256");
        var changed = new List<string>();
        foreach (var (name, recorded) in UpstreamLoginTemplates.Read(hashes))
        {
            var upstream = await realm.ReadUpstreamLoginTemplateAsync(name, CancellationToken);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(upstream)), recorded, StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        changed.Should().BeEmpty(
            $"Keycloak {KeycloakRealm.Version} changed these keycloak.v2 templates that our theme copies; merge the change into "
            + "our copy, then update its hash and the version in upstream-templates.sha256");
    }

    [Fact]
    public async Task AccessToken_ForTheControlPlaneScope_IsAcceptedByTheControlPlaneOnlyAsync()
    {
        var tokens = await realm.SignInAsync("alice@example.test", "openid control-plane");
        var accountId = await realm.UserIdAsync("alice@example.test");
        await using var controlPlane = AccessTokenAuthenticationTests.CreateApp(Environments.Development, realm.Url, signingKey: null, Audiences.ControlPlane);
        await using var cell = AccessTokenAuthenticationTests.CreateApp(Environments.Development, realm.Url, signingKey: null, Audiences.Cell("a"));
        await controlPlane.StartAsync(CancellationToken);
        await cell.StartAsync(CancellationToken);

        var accepted = await GetMeAsync(controlPlane, tokens.AccessToken);
        var otherAudience = await GetMeAsync(cell, tokens.AccessToken);
        var idToken = await GetMeAsync(controlPlane, tokens.IdToken);

        accepted.Should().Be((HttpStatusCode.OK, accountId));
        otherAudience.Status.Should().Be(HttpStatusCode.Unauthorized, "a control-plane token must not open a cell");
        idToken.Status.Should().Be(HttpStatusCode.Unauthorized, "an ID token is not an access token");
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(tokens.AccessToken).Audiences
            .Should().Equal([Audiences.ControlPlane], "a token names exactly one target");
    }

    [Fact]
    public async Task AccessToken_WithoutTheControlPlaneScope_IsRejectedByTheControlPlaneAsync()
    {
        var tokens = await realm.SignInAsync("bob@example.test", "openid");
        await using var controlPlane = AccessTokenAuthenticationTests.CreateApp(Environments.Development, realm.Url, signingKey: null, Audiences.ControlPlane);
        await controlPlane.StartAsync(CancellationToken);

        var response = await GetMeAsync(controlPlane, tokens.AccessToken);

        response.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetMeAsync(WebApplication app, string token)
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/me", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(CancellationToken));
    }
}
