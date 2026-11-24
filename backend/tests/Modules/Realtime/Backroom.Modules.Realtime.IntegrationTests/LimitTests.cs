using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>The per-connection and per-account bounds, set low for the nodes of these tests.</summary>
[Collection(CellCollection.Name)]
public sealed class LimitTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    protected override IReadOnlyDictionary<string, string?>? Settings { get; } = new Dictionary<string, string?>
    {
        ["Realtime:MaxSubscriptionsPerConnection"] = "3",
        ["Realtime:MaxConnectionsPerAccount"] = "2",
    };

    [Fact]
    public async Task Subscribe_BeyondTheLimit_IsRefusedUntilAGroupIsLeftAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var first = await CreateChannelAsync(NodeA, organizationId, Alice, "one");
        var second = await CreateChannelAsync(NodeA, organizationId, Alice, "two");
        var client = await ConnectAsync(NodeA, Alice);
        await client.SubscribeOrganizationAsync(organizationId);
        await client.SubscribeConversationAsync(organizationId, first);

        var failure = await Assert.ThrowsAsync<HubException>(() => client.SubscribeConversationAsync(organizationId, second));
        await client.UnsubscribeConversationAsync(organizationId, first);
        await client.SubscribeConversationAsync(organizationId, second);

        failure.Message.Should().EndWith("realtime.subscription_limit");
    }

    [Fact]
    public async Task SubscribeOrganization_WithRoomForOnlyOneGroup_IsRefusedBecauseItJoinsTwoAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var first = await CreateChannelAsync(NodeA, organizationId, Alice, "one");
        var second = await CreateChannelAsync(NodeA, organizationId, Alice, "two");
        var client = await ConnectAsync(NodeA, Alice);
        await client.SubscribeConversationAsync(organizationId, first);
        await client.SubscribeConversationAsync(organizationId, second);

        // The organization group and the account group count together; neither is joined alone.
        var failure = await Assert.ThrowsAsync<HubException>(() => client.SubscribeOrganizationAsync(organizationId));

        failure.Message.Should().EndWith("realtime.subscription_limit");
        (await SubscriptionCountAsync(NodeA, Alice)).Should().Be(2);
    }

    [Fact]
    public async Task Connect_BeyondTheLimitOfAnAccount_ClosesTheExtraConnectionAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        // The handshake completes before the node registers a connection (OnConnectedAsync), so a connected client is not yet
        // counted. A call is dispatched only after the registration, so its answer proves the connection holds a place; without
        // it, the third connection can be registered first and one of these is the one refused.
        var first = await ConnectAsync(NodeA, Alice);
        await first.SubscribeOrganizationAsync(organizationId);
        var second = await ConnectAsync(NodeA, Alice);
        await second.SubscribeOrganizationAsync(organizationId);

        var extra = await ConnectAsync(NodeA, Alice);

        await extra.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
        first.State.Should().Be(HubConnectionState.Connected);
        second.State.Should().Be(HubConnectionState.Connected);

        // The limit is per node: the same account may connect to the other node.
        var other = await ConnectAsync(NodeB, Alice);
        other.State.Should().Be(HubConnectionState.Connected);
    }
}
