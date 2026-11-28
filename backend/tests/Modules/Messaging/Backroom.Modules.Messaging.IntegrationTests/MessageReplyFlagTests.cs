using System.Net;
using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>With <c>Features:MessageReply</c> off (the default) a send with <c>replyToMessageId</c> is refused, never stripped.</summary>
[Collection(CellCollection.Name)]
public sealed class MessageReplyFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task Send_WithReplyToMessageIdWhenTheFlagIsOff_IsForbiddenAndStoresNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var original = (await SendMessageAsync(organizationId, channelId, Alice, "original"))["id"]!.GetValue<Guid>();

        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice,
            new { clientMessageId = Guid.NewGuid(), text = "reply", replyToMessageId = original });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("messaging.message_reply_unavailable");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId)).Should().Equal(1L);
    }
}
