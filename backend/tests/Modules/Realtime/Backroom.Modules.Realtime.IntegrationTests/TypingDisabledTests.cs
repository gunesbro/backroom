using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>With <c>Features:Typing</c> off (the default outside development) the hub refuses <c>Typing</c> as not found.</summary>
[Collection(CellCollection.Name)]
public sealed class TypingDisabledTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task Typing_WithTheFlagOff_IsRefusedAsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.TypingAsync(organizationId, channelId));

        failure.Message.Should().EndWith("realtime.not_found");
    }
}
