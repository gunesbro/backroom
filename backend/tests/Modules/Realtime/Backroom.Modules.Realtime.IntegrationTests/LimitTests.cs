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
        ["Realtime:MaxSubscriptionsPerConnection"] = "2",
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
        await client.SubscribeChannelAsync(organizationId, first);

        var failure = await Assert.ThrowsAsync<HubException>(() => client.SubscribeChannelAsync(organizationId, second));
        await client.UnsubscribeChannelAsync(organizationId, first);
        await client.SubscribeChannelAsync(organizationId, second);

        failure.Message.Should().EndWith("realtime.subscription_limit");
    }

    [Fact]
    public async Task Connect_BeyondTheLimitOfAnAccount_ClosesTheExtraConnectionAsync()
    {
        await ProvisionForAsync(Alice);
        await ConnectAsync(NodeA, Alice);
        await ConnectAsync(NodeA, Alice);

        var extra = await ConnectAsync(NodeA, Alice);

        await extra.Closed.WaitAsync(HubClient.Deadline, CancellationToken);

        // The limit is per node: the same account may connect to the other node.
        var other = await ConnectAsync(NodeB, Alice);
        other.State.Should().Be(HubConnectionState.Connected);
    }
}
