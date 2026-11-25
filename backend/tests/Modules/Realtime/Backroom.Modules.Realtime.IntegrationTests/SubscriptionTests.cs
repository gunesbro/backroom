using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// What a client may subscribe to: an active member of the organization, and for a conversation read access to it (an open
/// public channel, or an open conversation the account is a member of). Every refusal about existence or membership is the same code, so nothing leaks.
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
    public async Task SubscribeConversation_AsAMemberWhoIsNoLongerActive_IsRefusedAsNotFoundAsync(string status)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        await ExecuteAsync("UPDATE organization.members SET status = $1 WHERE org_id = $2 AND account_id = $3", status, organizationId, await AccountAsync(Bob));
        var bob = await ConnectAsync(NodeA, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeConversationAsync(organizationId, channelId));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeConversation_WithAChannelOfAnotherOrganization_IsRefusedAsNotFoundAsync()
    {
        var aliceOrganization = await ProvisionForAsync(Alice);
        var bobOrganization = await ProvisionForAsync(Bob);
        var aliceChannel = await CreateChannelAsync(NodeA, aliceOrganization, Alice, "general");
        var bob = await ConnectAsync(NodeA, Bob);

        // Bob is a member of his own organization, but names Alice's channel in it, and Alice's organization with it.
        var inOwnOrganization = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeConversationAsync(bobOrganization, aliceChannel));
        var inAlicesOrganization = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeConversationAsync(aliceOrganization, aliceChannel));

        inOwnOrganization.Message.Should().EndWith(NotFound);
        inAlicesOrganization.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeConversation_WithAChannelThatDoesNotExist_IsRefusedAsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.SubscribeConversationAsync(organizationId, Guid.NewGuid()));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task SubscribeConversation_WithAnArchivedConversation_FollowsReadAccessAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "old");
        var secret = await CreatePrivateConversationAsync(organizationId, Alice);
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = ANY($1)", new[] { channelId, secret });
        var alice = await ConnectAsync(NodeA, Alice);
        var bob = await ConnectAsync(NodeB, Bob);

        // Archived conversations stay readable (so an unarchive reaches their readers); archiving opens nothing.
        await alice.SubscribeConversationAsync(organizationId, channelId);
        await alice.SubscribeConversationAsync(organizationId, secret);
        await bob.SubscribeConversationAsync(organizationId, channelId);
        var failure = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeConversationAsync(organizationId, secret));

        failure.Message.Should().EndWith(NotFound, "an Admin who is no member cannot join an archived private channel's group");
    }

    [Fact]
    public async Task SubscribeConversation_WithAPrivateChannel_IsAllowedOnlyForItsMembersOnEveryNodeAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var privateId = await CreatePrivateConversationAsync(organizationId, Bob);
        var aliceOnA = await ConnectAsync(NodeA, Alice);
        var aliceOnB = await ConnectAsync(NodeB, Alice);
        var bob = await ConnectAsync(NodeA, Bob);

        // Alice holds the Owner role, which gives no access to a conversation she is not in, on either node.
        var onA = await Assert.ThrowsAsync<HubException>(() => aliceOnA.SubscribeConversationAsync(organizationId, privateId));
        var onB = await Assert.ThrowsAsync<HubException>(() => aliceOnB.SubscribeConversationAsync(organizationId, privateId));
        await bob.SubscribeConversationAsync(organizationId, privateId);

        onA.Message.Should().EndWith(NotFound);
        onB.Message.Should().EndWith(NotFound);
        (await SubscriptionCountAsync(NodeA, Alice)).Should().Be(0);
        (await SubscriptionCountAsync(NodeB, Alice)).Should().Be(0);
    }

    [Fact]
    public async Task SubscribeConversation_AsAnActiveMemberWithoutARole_IsAllowedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, role: null);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var bob = await ConnectAsync(NodeB, Bob);

        await bob.SubscribeConversationAsync(organizationId, channelId);
        await bob.SubscribeConversationAsync(organizationId, channelId); // idempotent

        await SendMessageAsync(NodeA, organizationId, channelId, Alice, "hi");
        (await bob.NextMessageAsync()).Message.Text.Should().Be("hi");
        bob.HasUnreadMessage.Should().BeFalse("subscribing twice joins once");
    }

    [Fact]
    public async Task Subscribe_WithAnEmptyId_IsRefusedAsInvalidAsync()
    {
        var alice = await ConnectAsync(NodeA, Alice);

        (await Assert.ThrowsAsync<HubException>(() => alice.SubscribeOrganizationAsync(Guid.Empty))).Message.Should().EndWith("realtime.invalid_argument");
        (await Assert.ThrowsAsync<HubException>(() => alice.SubscribeConversationAsync(Guid.NewGuid(), Guid.Empty))).Message.Should().EndWith("realtime.invalid_argument");
    }

    [Fact]
    public async Task Refusal_ForAnyReason_ExposesNoInternalsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var bob = await ConnectAsync(NodeA, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.SubscribeConversationAsync(organizationId, Guid.NewGuid()));

        // SignalR adds the exception type name for a HubException; nothing else but the code.
        failure.Message.Should().Be("An unexpected error occurred invoking 'SubscribeConversation' on the server. HubException: realtime.not_found");
        failure.Message.Should().NotContainAny("Npgsql", "postgres", "   at ", organizationId.ToString());
    }
}
