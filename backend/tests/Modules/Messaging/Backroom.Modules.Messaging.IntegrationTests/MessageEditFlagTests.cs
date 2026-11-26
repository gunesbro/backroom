using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>With <c>Features:MessageEdit</c> off (the default) edit and delete are refused and nothing changes.</summary>
[Collection(CellCollection.Name)]
public sealed class MessageEditFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task EditAndDelete_WhenTheFlagIsOff_AreForbiddenAndChangeNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "untouched");
        var messageId = sent["id"]!.GetValue<Guid>();

        using var edit = await SendAsync(
            HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/messages/{messageId}", Alice,
            new { blocks = JsonNode.Parse("""[{"type":"paragraph","children":[{"type":"text","text":"changed"}]}]""") });
        using var delete = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/messages/{messageId}", Alice, null);

        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(delete)).Should().Be("messaging.message_edit_unavailable");
        (await QuerySingleAsync("SELECT text, version_seq, deleted_at IS NULL FROM messaging.messages WHERE id = $1", messageId)).Should().Equal("untouched", 1L, true);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1L);
    }
}
