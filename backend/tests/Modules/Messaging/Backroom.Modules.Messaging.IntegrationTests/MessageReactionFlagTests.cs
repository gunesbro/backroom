using System.Net;
using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>With <c>Features:MessageReactions</c> off (the default) the reaction endpoints answer 404, nothing changes and reads carry no reactions.</summary>
[Collection(CellCollection.Name)]
public sealed class MessageReactionFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task PutAndDelete_WhenTheFlagIsOff_AreNotFoundAndChangeNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "untouched"))["id"]!.GetValue<Guid>();
        var path = $"/api/v1/orgs/{organizationId}/messages/{messageId}/reactions/{Uri.EscapeDataString("\U0001F44D")}";

        using var put = await SendAsync(HttpMethod.Put, path, Alice, null);
        using var delete = await SendAsync(HttpMethod.Delete, path, Alice, null);

        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(put)).Should().Be("messaging.reactions_unavailable");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
        (await QuerySingleAsync("SELECT version_seq FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(1L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1L);
        Publisher.Reactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_WhenTheFlagIsOff_CarriesNoReactionsEvenForStoredOnesAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "has a stored reaction");
        var messageId = sent["id"]!.GetValue<Guid>();
        await ExecuteAsync(
            "INSERT INTO messaging.reactions (org_id, message_id, account_id, emoji, created_at) VALUES ($1, $2, $3, $4, now())",
            organizationId, messageId, await AccountAsync(Alice), "\U0001F44D");
        await ExecuteAsync(
            "UPDATE messaging.messages SET reaction_summary = jsonb_build_array(jsonb_build_object('emoji', $2::text, 'count', 1, 'sample', jsonb_build_array($3::text))) WHERE id = $1",
            messageId, "\U0001F44D", (await AccountAsync(Alice)).ToString());

        using var page = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("reactions");
        sent.AsObject().ContainsKey("reactions").Should().BeFalse("the send answer carries none either");
    }
}
