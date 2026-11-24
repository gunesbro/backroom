using System.Net;
using AwesomeAssertions;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.EndToEndTests;

/// <summary>
/// Flow 5: signing out ends the session on every cell where the person is a member: Keycloak calls the control plane
/// back-channel, which tells the cells over the link; they close the session's live connections and refuse its tokens.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Short)]
public sealed class SignOutTests(EndToEndEnvironment environment)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignOut_WithLiveConnectionsOnBothCells_RevokesOnlyThatSessionEverywhereAsync()
    {
        Gate.RequireEnvironment();
        var organizations = await environment.OrganizationsAsync(CancellationToken);
        var cellIds = await environment.SharedCellIdsAsync(CancellationToken);
        using var leaving = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);
        using var staying = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);
        var sessions = new List<(TestOrganization Organization, AccessToken Token, LiveClient Live)>();
        try
        {
            foreach (var organization in organizations)
            {
                var token = await leaving.TokenForAsync("cell:" + organization.CellId, CancellationToken);
                var live = await LiveClient.ConnectAsync(organization.CellUrl, token, CancellationToken);
                sessions.Add((organization, token, live));
                await live.SubscribeOrganizationAsync(organization.Id, CancellationToken);
                (await ChannelsStatusAsync(organization, token)).Should().Be(HttpStatusCode.OK);
            }

            await leaving.SignOutAsync(CancellationToken);

            foreach (var (organization, token, live) in sessions)
            {
                var revoked = await live.NextSessionRevokedAsync(CancellationToken);
                revoked.Reason.Should().Be(SessionRevokedPayload.SessionEnded);
                await Eventually.WithinDeadlineAsync(live.Closed, $"Cell {organization.CellId} closing the connection", CancellationToken);
                await Eventually.UntilAsync(
                    _ => ChannelsStatusAsync(organization, token),
                    status => status == HttpStatusCode.Unauthorized,
                    $"Cell {organization.CellId} refusing the ended session's token",
                    CancellationToken);

                // Another session of the same person goes on working.
                var other = await staying.TokenForAsync("cell:" + organization.CellId, CancellationToken);
                (await ChannelsStatusAsync(organization, other)).Should().Be(HttpStatusCode.OK);
            }
        }
        finally
        {
            foreach (var (_, _, live) in sessions)
            {
                await live.DisposeAsync();
            }
        }
    }

    private async Task<HttpStatusCode> ChannelsStatusAsync(TestOrganization organization, AccessToken token) =>
        (await Api.GetAsync(environment.Http, organization.Api("channels"), token.Value, CancellationToken)).Status;
}
