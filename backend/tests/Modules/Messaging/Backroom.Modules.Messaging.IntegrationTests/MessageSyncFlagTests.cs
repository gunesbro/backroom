using System.Net;
using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// <c>Features:MessageSync</c> on (docs/contracts/phase-2.md, "Sequence numbers" and "Live events"): every cursor needs
/// the cell epoch, and a send announces <c>MessagePosted.v2</c> with the change number and the epoch.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class MessageSyncFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    protected override bool MessageSyncEnabled => true;

    [Theory]
    [InlineData("after=1", HttpStatusCode.Conflict)]
    [InlineData("before=2", HttpStatusCode.Conflict)]
    [InlineData("changed_after=1", HttpStatusCode.Conflict)]
    [InlineData("after=1&epoch=6", HttpStatusCode.Conflict)]
    [InlineData("after=1&epoch=7", HttpStatusCode.OK)]
    [InlineData("before=2&epoch=7", HttpStatusCode.OK)]
    [InlineData("changed_after=1&epoch=7", HttpStatusCode.OK)]
    [InlineData("limit=1", HttpStatusCode.OK)]
    public async Task List_WithACursor_NeedsTheEpochAsync(string query, HttpStatusCode expected)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        await SendMessageAsync(organizationId, channelId, Alice, "one");
        await SendMessageAsync(organizationId, channelId, Alice, "two");

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?{query}", Alice);

        response.StatusCode.Should().Be(expected);
        (await ReadJsonAsync(response))[expected == HttpStatusCode.OK ? "epoch" : "code"]!.GetValue<string>()
            .Should().Be(expected == HttpStatusCode.OK ? "7" : "messaging.resync_required");
    }

    [Fact]
    public async Task Send_AnnouncesMessagePostedV2WithTheNumbersAndTheEpochAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        await SendMessageAsync(organizationId, channelId, Alice, "one");
        var clientMessageId = Guid.NewGuid();

        var sent = await SendMessageAsync(organizationId, channelId, Alice, "two", clientMessageId);
        await SendMessageAsync(organizationId, channelId, Alice, "two", clientMessageId);

        Publisher.Messages.Should().BeEmpty("the new event replaces the Phase 1 one");
        Publisher.MessagesV2.Should().HaveCount(2, "a repeat announces nothing");
        var published = Publisher.MessagesV2[1];
        published.OrgId.Should().Be(organizationId);
        published.ConversationId.Should().Be(channelId);
        published.ChangeSeq.Should().Be(2);
        published.Epoch.Should().Be("7");
        published.Message.Id.Should().Be(sent["id"]!.GetValue<Guid>());
        published.Message.Seq.Should().Be(2);
        published.Message.VersionSeq.Should().Be(2);
        published.Message.Kind.Should().Be("text");
        published.Message.ClientMessageId.Should().Be(clientMessageId);
        published.Message.AuthorAccountId.Should().Be(await AccountAsync(Alice));
        published.Message.Text.Should().Be("two");
    }
}
