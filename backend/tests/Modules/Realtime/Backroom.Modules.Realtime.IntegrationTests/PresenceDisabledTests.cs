using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>With <c>Features:Presence</c> off (the default outside development) the hub refuses the presence methods and the read endpoint is not mapped.</summary>
[Collection(CellCollection.Name)]
public sealed class PresenceDisabledTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task Presence_WithTheFlagOff_IsRefusedOnTheHubAndNotMappedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await ConnectAsync(NodeA, Alice);

        var watch = await Assert.ThrowsAsync<HubException>(() => alice.WatchPresenceAsync(organizationId, Guid.NewGuid()));
        var report = await Assert.ThrowsAsync<HubException>(() => alice.ReportActivityAsync("active"));
        using var read = await GetAsync(NodeA, $"/api/v1/orgs/{organizationId}/presence?accountIds={Guid.NewGuid()}", Alice);

        watch.Message.Should().EndWith("realtime.not_found");
        report.Message.Should().EndWith("realtime.not_found");
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
