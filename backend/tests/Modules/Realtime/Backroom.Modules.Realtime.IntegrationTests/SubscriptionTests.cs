using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// What a client may subscribe to: an active member of the organization, and for a channel one that exists, is public and
/// is not archived. Every refusal about existence or membership is the same code, so nothing leaks.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class SubscriptionTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    private const string NotFound = "realtime.not_found";

    [Fact]
    public async Task SubscribeOrganization_AsANonMember_IsRefusedAsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var bob = await ConnectAsync(NodeA, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeOrganizationAsync(organizationId));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeOrganization_ForAnOrganizationThatDoesNotExist_IsRefusedAsNotFoundAsync()
    {
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.SubscribeOrganizationAsync(Guid.NewGuid()));

        failure.Message.Should().EndWith(NotFound);
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("removed")]
    public async Task SubscribeChannel_AsAMemberWhoIsNoLongerActive_IsRefusedAsNotFoundAsync(string status)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        await ExecuteAsync("UPDATE organization.members SET status = $1 WHERE org_id = $2 AND account_id = $3", status, organizationId, await AccountAsync(Bob));
        var bob = await ConnectAsync(NodeA, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeChannelAsync(organizationId, channelId));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeChannel_WithAChannelOfAnotherOrganization_IsRefusedAsNotFoundAsync()
    {
        var aliceOrganization = await ProvisionForAsync(Alice);
        var bobOrganization = await ProvisionForAsync(Bob);
        var aliceChannel = await CreateChannelAsync(NodeA, aliceOrganization, Alice, "general");
        var bob = await ConnectAsync(NodeA, Bob);

        // Bob is a member of his own organization, but names Alice's channel in it, and Alice's organization with it.
        var inOwnOrganization = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeChannelAsync(bobOrganization, aliceChannel));
        var inAlicesOrganization = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeChannelAsync(aliceOrganization, aliceChannel));

        inOwnOrganization.Message.Should().EndWith(NotFound);
        inAlicesOrganization.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeChannel_WithAChannelThatDoesNotExist_IsRefusedAsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.SubscribeChannelAsync(organizationId, Guid.NewGuid()));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeChannel_WithAnArchivedChannel_IsRefusedAsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "old");
        await ExecuteAsync("UPDATE messaging.channels SET archived_at = now() WHERE id = $1", channelId);
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.SubscribeChannelAsync(organizationId, channelId));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeChannel_AsAnActiveMemberWithoutARole_IsAllowedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, role: null);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var bob = await ConnectAsync(NodeB, Bob);

        await bob.SubscribeChannelAsync(organizationId, channelId);
        await bob.SubscribeChannelAsync(organizationId, channelId); // idempotent

        await SendMessageAsync(NodeA, organizationId, channelId, Alice, "hi");
        (await bob.NextMessageAsync()).Message.Text.Should().Be("hi");
        bob.HasUnreadMessage.Should().BeFalse("subscribing twice joins once");
    }

    [Fact]
    public async Task Subscribe_WithAnEmptyId_IsRefusedAsInvalidAsync()
    {
        var alice = await ConnectAsync(NodeA, Alice);

        (await Assert.ThrowsAsync<HubException>(() => alice.SubscribeOrganizationAsync(Guid.Empty))).Message.Should().EndWith("realtime.invalid_argument");
        (await Assert.ThrowsAsync<HubException>(() => alice.SubscribeChannelAsync(Guid.NewGuid(), Guid.Empty))).Message.Should().EndWith("realtime.invalid_argument");
    }

    [Fact]
    public async Task Refusal_ForAnyReason_ExposesNoInternalsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var bob = await ConnectAsync(NodeA, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeChannelAsync(organizationId, Guid.NewGuid()));

        // SignalR adds the exception type name for a HubException; nothing else but the code.
        failure.Message.Should().Be("An unexpected error occurred invoking 'SubscribeChannel' on the server. HubException: realtime.not_found");
        failure.Message.Should().NotContainAny("Npgsql", "postgres", "   at ", organizationId.ToString());
    }
}
