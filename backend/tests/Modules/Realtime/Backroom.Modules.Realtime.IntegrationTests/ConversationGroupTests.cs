using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// Conversation and account groups (docs/contracts/phase-2.md, "Live events"): a removal from a conversation takes the
/// account's connections out of that conversation's group on every node, by Messaging's after-commit call and by the
/// consumer of <c>messaging.conversation-member-removed.v1</c>; an account group only ever holds that account's connections.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class ConversationGroupTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConversationMemberRemoved_WithConnectionsOnBothNodes_RemovesThemFromThatConversationOnlyAsync(bool byConsumer)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var secret = await CreatePrivateConversationAsync(organizationId, Alice, Bob);
        var general = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var alice = await ConnectAsync(NodeA, Alice);
        await alice.SubscribeConversationAsync(organizationId, secret);
        var bobOnA = await ConnectAsync(NodeA, Bob);
        var bobOnB = await ConnectAsync(NodeB, Bob);
        foreach (var bob in new[] { bobOnA, bobOnB })
        {
            await bob.SubscribeOrganizationAsync(organizationId);
            await bob.SubscribeConversationAsync(organizationId, secret);
            await bob.SubscribeConversationAsync(organizationId, general);
        }

        await EndMembershipAsync(organizationId, secret, Bob);
        await RemoveAsync(organizationId, secret, Bob, byConsumer);
        await WaitForSubscriptionsAsync(Bob, expected: 3);

        // Alice proves the path works; Bob's clients got nothing from the private conversation.
        await PublishMessageAsync(organizationId, secret, "after the removal");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("after the removal");
        bobOnA.HasUnreadMessage.Should().BeFalse();
        bobOnB.HasUnreadMessage.Should().BeFalse();

        // His other conversation and his organization groups are untouched, on both nodes.
        await SendMessageAsync(NodeB, organizationId, general, Alice, "still here");
        (await bobOnA.NextMessageAsync()).Message.Text.Should().Be("still here");
        (await bobOnB.NextMessageAsync()).Message.Text.Should().Be("still here");

        // Joining again needs the membership he no longer has; a second removal changes nothing.
        (await Assert.ThrowsAsync<HubException>(() => bobOnB.SubscribeConversationAsync(organizationId, secret))).Message.Should().EndWith("realtime.not_found");
        await RemoveAsync(organizationId, secret, Bob, byConsumer);
        await WaitForSubscriptionsAsync(Bob, expected: 3);
    }

    [Fact]
    public async Task ConversationMemberRemoved_ForAnotherAccount_LeavesTheGroupAloneAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var secret = await CreatePrivateConversationAsync(organizationId, Alice, Bob);
        var alice = await ConnectAsync(NodeB, Alice);
        await alice.SubscribeConversationAsync(organizationId, secret);

        await RemoveAsync(organizationId, secret, Bob, byConsumer: true);
        // The listeners handle messages in order, so a later one proves the first was handled.
        await RemoveAsync(organizationId, Guid.CreateVersion7(), Bob, byConsumer: true);

        await PublishMessageAsync(organizationId, secret, "still mine");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("still mine");
        (await SubscriptionCountAsync(NodeB, Alice)).Should().Be(1);
    }

    [Fact]
    public async Task AccountGroup_OnBothNodes_HoldsOnlyThatAccountsSubscribedConnectionsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var aliceOnA = await ConnectAsync(NodeA, Alice);
        var aliceOnB = await ConnectAsync(NodeB, Alice);
        var aliceLater = await ConnectAsync(NodeB, Alice);
        var bob = await ConnectAsync(NodeA, Bob);
        await aliceOnA.SubscribeOrganizationAsync(organizationId);
        await aliceOnB.SubscribeOrganizationAsync(organizationId);
        await bob.SubscribeOrganizationAsync(organizationId);
        var alicesGroup = await AccountGroupAsync(organizationId, Alice);

        await ProbeAsync(alicesGroup, "for alice");
        await ProbeAsync(await AccountGroupAsync(organizationId, Bob), "for bob");

        (await aliceOnA.NextProbeAsync()).Should().Be("for alice");
        (await aliceOnB.NextProbeAsync()).Should().Be("for alice");
        (await bob.NextProbeAsync()).Should().Be("for bob", "Bob is never in Alice's group");
        aliceOnA.HasUnreadProbe.Should().BeFalse();
        aliceOnB.HasUnreadProbe.Should().BeFalse();

        // A connection joins the account group only through the organization's membership check.
        await aliceLater.SubscribeOrganizationAsync(organizationId);
        await ProbeAsync(alicesGroup, "second");
        (await aliceLater.NextProbeAsync()).Should().Be("second");
        (await aliceOnA.NextProbeAsync()).Should().Be("second");

        // Leaving the organization's groups leaves the account group too.
        await aliceOnA.UnsubscribeOrganizationAsync(organizationId);
        await ProbeAsync(alicesGroup, "third");
        (await aliceOnB.NextProbeAsync()).Should().Be("second");
        (await aliceOnB.NextProbeAsync()).Should().Be("third");
        aliceOnA.HasUnreadProbe.Should().BeFalse();
        (await SubscriptionCountAsync(NodeA, Alice)).Should().Be(0);
    }

    [Fact]
    public async Task AccountGroup_AfterTheMemberIsRemovedFromTheOrganization_IsLeftOnBothNodesAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bobOnA = await ConnectAsync(NodeA, Bob);
        var bobOnB = await ConnectAsync(NodeB, Bob);
        await bobOnA.SubscribeOrganizationAsync(organizationId);
        await bobOnB.SubscribeOrganizationAsync(organizationId);

        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob));
        await new MemberRemovedConsumer(NodeA.Services.GetRequiredService<GroupRemovalBroadcast>())
            .HandleAsync(new MemberRemoved(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, await AccountAsync(Bob), 2), CancellationToken);
        await WaitForSubscriptionsAsync(Bob, expected: 0);

        await ProbeAsync(await AccountGroupAsync(organizationId, Bob), "too late");
        // A probe to a group the clients are still in proves the first one would have arrived by now.
        var other = await ProvisionForAsync(Bob);
        await bobOnA.SubscribeOrganizationAsync(other);
        await bobOnB.SubscribeOrganizationAsync(other);
        await ProbeAsync(await AccountGroupAsync(other, Bob), "other organization");
        (await bobOnA.NextProbeAsync()).Should().Be("other organization");
        (await bobOnB.NextProbeAsync()).Should().Be("other organization");
    }

    // Messaging's after-commit call (any node), or the worker's consumer of the durable event.
    private async Task RemoveAsync(Guid organizationId, Guid conversationId, string username, bool byConsumer)
    {
        var account = await AccountAsync(username);
        if (byConsumer)
        {
            var consumer = new ConversationMemberRemovedConsumer(
                NodeA.Services.GetRequiredService<GroupRemovalBroadcast>(), NodeA.Services.GetRequiredService<IOptions<RealtimeOptions>>());
            await consumer.HandleAsync(
                new ConversationMemberRemoved(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, conversationId, account, account), CancellationToken);
        }
        else
        {
            await NodeB.Services.GetRequiredService<ILiveEventPublisher>().RemoveFromConversationAsync(organizationId, conversationId, account, CancellationToken);
        }
    }

    // Straight to the conversation's group: the send route serves public channels only until the conversation routes arrive (step 3).
    private Task PublishMessageAsync(Guid organizationId, Guid conversationId, string text) =>
        NodeA.Services.GetRequiredService<ILiveEventPublisher>().PublishAsync(
            new MessagePostedPayload(organizationId, conversationId, new LiveMessage(Guid.CreateVersion7(), 1, Guid.NewGuid(), Guid.NewGuid(), text, DateTimeOffset.UtcNow)),
            CancellationToken);

    private async Task<string> AccountGroupAsync(Guid organizationId, string username) =>
        GroupNames.Account(CellId, organizationId, await AccountAsync(username));

    // No module sends to account groups yet (read state and seen come later), so the test sends through the hub context.
    private Task ProbeAsync(string group, string text) =>
        NodeA.Services.GetRequiredService<IHubContext<RealtimeHub>>().Clients.Group(group).SendAsync(HubClient.ProbeEvent, text, CancellationToken);
}
