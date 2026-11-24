using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>A connection that keeps subscribing, or keeps asking for what it may not have, is cut off.</summary>
[Collection(CellCollection.Name)]
public sealed class AbuseLimitTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    protected override IReadOnlyDictionary<string, string?>? Settings { get; } = new Dictionary<string, string?>
    {
        ["Realtime:MaxSubscribeCallsPerWindow"] = "4",
        ["Realtime:SubscribeWindow"] = "01:00:00",
        ["Realtime:MaxRefusedSubscribesPerConnection"] = "2",
    };

    [Fact]
    public async Task Subscribe_BeyondTheCallsPerWindow_AbortsTheConnectionAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var client = await ConnectAsync(NodeA, Alice);
        for (var i = 0; i < 4; i++)
        {
            await client.SubscribeOrganizationAsync(organizationId);
        }

        // The abort can overtake the error message, so the client sees either the code or the closed connection.
        await Assert.ThrowsAnyAsync<Exception>(() => client.SubscribeOrganizationAsync(organizationId));

        await client.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
    }

    [Fact]
    public async Task Subscribe_AfterTooManyRefusals_AbortsTheConnectionAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var client = await ConnectAsync(NodeA, Bob);

        (await Assert.ThrowsAsync<HubException>(() => client.SubscribeOrganizationAsync(organizationId))).Message.Should().EndWith("realtime.not_found");
        await Assert.ThrowsAnyAsync<Exception>(() => client.SubscribeOrganizationAsync(organizationId));

        await client.Closed.WaitAsync(HubClient.Deadline, CancellationToken);
    }

    [Fact]
    public void TryCountCall_AfterTheWindow_StartsAgain()
    {
        var state = new ConnectionState("c", Guid.NewGuid());
        var start = DateTimeOffset.UnixEpoch;
        var window = TimeSpan.FromSeconds(10);

        state.TryCountCall(start, window, 2).Should().BeTrue();
        state.TryCountCall(start.AddSeconds(1), window, 2).Should().BeTrue();
        state.TryCountCall(start.AddSeconds(2), window, 2).Should().BeFalse();
        state.TryCountCall(start.AddSeconds(11), window, 2).Should().BeTrue();
    }
}
