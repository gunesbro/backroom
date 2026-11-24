using AwesomeAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>
/// The whole sign-out against a real Keycloak (docs/contracts/phase-1.md, flow 5): the realm file's clients carry the back-channel
/// logout URL, Keycloak ends the session and calls a control plane that validates the real logout token against the realm's real
/// JWKS, and the revocation reaches the cells where the person is a member. The control plane listens on this machine; the
/// container reaches it as <c>host.docker.internal</c>.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class KeycloakBackchannelLogoutTests(PostgresDatabase database, KeycloakRealm keycloak) : IClassFixture<KeycloakRealm>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logout_ByKeycloak_RevokesTheSessionOfTheAccessTokenAndTellsTheCellAsync()
    {
        const string User = "alice@example.test";
        var account = Guid.Parse(await keycloak.UserIdAsync(User));
        var tokens = await keycloak.SignInAsync(User, "openid control-plane");
        var sessionId = new JsonWebToken(tokens.AccessToken).GetPayloadValue<string>("sid");
        // The control plane first: it migrates the database the test then arranges.
        await using var controlPlane = await IdentityUnderTest.StartAsync(
            database, CancellationToken, TimeProvider.System, authority: keycloak.Url, listenOn: $"http://0.0.0.0:{keycloak.BackchannelLogoutPort}");
        var data = new TestData(database);
        var cell = await data.AddCellAsync();
        await data.AddMembershipAsync(account, cell);

        await keycloak.LogoutUserAsync(User);

        // Keycloak calls the control plane while the logout request is handled; allow it a moment.
        IReadOnlyList<ControlPlane.Contracts.V1.SessionRevoked> messages = [];
        for (var attempt = 0; attempt < 50 && messages.Count == 0; attempt++)
        {
            messages = await data.SessionRevokedMessagesAsync(cell);
            if (messages.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken);
            }
        }

        var message = messages.Should().ContainSingle("Keycloak sends one logout token per client session").Subject;
        message.AccountId.Should().Be(account.ToString("D"));
        message.SessionId.Should().Be(sessionId, "the logout token names the session of the access token");
        (await data.RevokedSessionAsync(account)).SessionId.Should().Be(sessionId);
    }
}
