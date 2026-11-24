using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Backroom.EndToEndTests;

/// <summary>
/// Flow 3 (nightly): an invitation travels through the cell, the control plane and the email (Mailpit), the second person
/// joins, and then receives the first person's messages live.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Nightly)]
public sealed partial class InvitationTests(EndToEndEnvironment environment)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Invite_ThroughEmail_LetsTheSecondPersonJoinAndReceiveMessagesLiveAsync()
    {
        Gate.RequireEnvironment();
        var organization = (await environment.OrganizationsAsync(CancellationToken)).Single(candidate => candidate.CellId == "a");
        var cellIds = await environment.SharedCellIdsAsync(CancellationToken);
        using var alice = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);
        var aliceCell = await alice.TokenForAsync("cell:a", CancellationToken);

        var invited = await Api.PostAsync(
            environment.Http, organization.Api("invitations"), aliceCell.Value, new { email = EndToEndEnvironment.Bob }, CancellationToken);
        invited.Status.Should().Be(HttpStatusCode.Created);
        var code = await InvitationCodeFromEmailAsync();

        using var bob = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Bob, cellIds, CancellationToken);
        var bobControlPlane = await bob.TokenForAsync("control-plane", CancellationToken);
        var resolved = await Api.PostAsync(environment.Http, environment.ControlPlane("/api/v1/invitations/resolve"), bobControlPlane.Value, new { code }, CancellationToken);
        resolved.Status.Should().Be(HttpStatusCode.OK);
        resolved.Json["matchesAccount"]!.GetValue<bool>().Should().BeTrue();
        var accepted = await Api.PostAsync(environment.Http, environment.ControlPlane("/api/v1/invitations/accept"), bobControlPlane.Value, new { code }, CancellationToken);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        var acceptance = await Eventually.UntilAsync(
            async token => (await Api.GetAsync(environment.Http, environment.ControlPlane("/api/v1/invitations/acceptance"), bobControlPlane.Value, token)).Json,
            latest => latest["status"]!.GetValue<string>() != "accepting",
            "The cell settling the acceptance",
            CancellationToken);
        acceptance["status"]!.GetValue<string>().Should().Be("used");

        // Bob, now a member, reads the channel live while Alice writes.
        var channel = await Api.PostAsync(
            environment.Http, organization.Api("channels"), aliceCell.Value, new { name = $"e2e-{Guid.NewGuid():N}"[..20] }, CancellationToken);
        channel.Status.Should().Be(HttpStatusCode.Created);
        var channelId = channel.Json["id"]!.GetValue<Guid>();
        var bobCell = await bob.TokenForAsync("cell:a", CancellationToken);
        await using var bobLive = await LiveClient.ConnectAsync(organization.CellUrl, bobCell, CancellationToken);
        await bobLive.SubscribeChannelAsync(organization.Id, channelId, CancellationToken);
        var clientMessageId = Guid.NewGuid();
        var posted = await Api.PostAsync(
            environment.Http, organization.Api($"channels/{channelId}/messages"), aliceCell.Value, new { clientMessageId, text = "Welcome, Bob" }, CancellationToken);
        posted.Status.Should().Be(HttpStatusCode.Created);

        var received = await bobLive.NextMessageAsync(CancellationToken);
        received.Message.ClientMessageId.Should().Be(clientMessageId);
        received.Message.Text.Should().Be("Welcome, Bob");
    }

    // The invitation email reaches Mailpit only after the control plane registered the code (flow 3, step 3).
    private async Task<string> InvitationCodeFromEmailAsync()
    {
        var query = Uri.EscapeDataString($"to:\"{EndToEndEnvironment.Bob}\"");
        var messageId = await Eventually.UntilAsync(
            async token =>
            {
                using var response = await environment.Http.GetAsync(new Uri(environment.MailpitUrl, $"/api/v1/search?query={query}"), token);
                var found = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?["messages"]?.AsArray();
                return found is { Count: > 0 } ? found[0]!["ID"]!.GetValue<string>() : null;
            },
            id => id is not null,
            "The invitation email arriving",
            CancellationToken);
        using var message = await environment.Http.GetAsync(new Uri(environment.MailpitUrl, $"/api/v1/message/{Uri.EscapeDataString(messageId!)}"), CancellationToken);
        var text = JsonNode.Parse(await message.Content.ReadAsStringAsync(CancellationToken))!["Text"]!.GetValue<string>();
        var link = InvitationLink().Match(text);
        link.Success.Should().BeTrue("the invitation email carries the invitation link");
        return link.Groups["code"].Value;
    }

    [GeneratedRegex(@"https?://\S+/invite#(?<code>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex InvitationLink();
}
