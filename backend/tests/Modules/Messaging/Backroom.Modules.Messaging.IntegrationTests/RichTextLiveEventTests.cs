using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MessagePack;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// With <c>Features:RichText</c> on and <c>Features:MessageSync</c> off, the live <c>MessagePosted</c> carries the
/// message's blocks too (an additive field), so a client that does not read <c>MessagePosted.v2</c> still shows formatting.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class RichTextLiveEventTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    protected override bool RichTextEnabled => true;

    [Fact]
    public async Task Send_WithBlocks_PublishesMessagePostedWithTheBlocksAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        const string Blocks = """[{"type":"paragraph","children":[{"type":"text","text":"hi "},{"type":"text","text":"there","marks":[{"type":"bold"}]}]}]""";

        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages",
            Alice,
            new { clientMessageId = Guid.NewGuid(), blocks = JsonNode.Parse(Blocks) });
        await SendMessageAsync(organizationId, channelId, Alice, "plain");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Publisher.MessagesV2.Should().BeEmpty();
        Publisher.Messages.Should().HaveCount(2);
        var live = Publisher.Messages[0].Message;
        live.Text.Should().Be("hi there");
        var json = JsonNode.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(live.Blocks!, cancellationToken: CancellationToken), cancellationToken: CancellationToken));
        JsonNode.DeepEquals(json, JsonNode.Parse(Blocks)).Should().BeTrue($"the live blocks were {json?.ToJsonString()}");
        Publisher.Messages[1].Message.Blocks.Should().NotBeNull("a plain text send has one paragraph");
    }
}
