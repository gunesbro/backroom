using System.Net;
using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>With <c>Features:Conversations</c> off (the default outside development) the conversation routes do not exist.</summary>
[Collection(CellCollection.Name)]
public sealed class ConversationFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    protected override bool ConversationsEnabled => false;

    [Fact]
    public async Task JoinAndLeave_WhenTheFlagIsOff_AreNotMappedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");

        using var join = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Alice, null);
        using var leave = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Alice, null);

        join.StatusCode.Should().Be(HttpStatusCode.NotFound);
        leave.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/api/v1/orgs/{organizationId}/channels/{channelId}/messages", Alice)).StatusCode.Should().Be(HttpStatusCode.OK, "Phase 1 routes stay");
    }
}
