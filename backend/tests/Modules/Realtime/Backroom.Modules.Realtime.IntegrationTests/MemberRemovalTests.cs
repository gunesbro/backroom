using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// <c>organization.member-removed.v1</c> takes the account's connections out of that organization's groups on every
/// app node, and only that organization's. The consumer runs here as in the worker (it publishes to Valkey); the nodes
/// are the app role.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class MemberRemovalTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task MemberRemoved_WithConnectionsOnBothNodes_RemovesThemFromTheOrganizationsGroupsOnlyAsync()
    {
        var first = await ProvisionForAsync(Alice);
        await AddMemberAsync(first, Bob, SystemRoles.Member);
        var second = await ProvisionForAsync(Bob);
        var firstChannel = await CreateChannelAsync(NodeA, first, Alice, "general");
        var secondChannel = await CreateChannelAsync(NodeA, second, Bob, "team");

        var alice = await ConnectAsync(NodeB, Alice);
        await alice.SubscribeConversationAsync(first, firstChannel);
        await alice.SubscribeOrganizationAsync(first);
        var bobOnA = await ConnectAsync(NodeA, Bob);
        var bobOnB = await ConnectAsync(NodeB, Bob);
        foreach (var bob in new[] { bobOnA, bobOnB })
        {
            await bob.SubscribeOrganizationAsync(first);
            await bob.SubscribeConversationAsync(first, firstChannel);
            await bob.SubscribeConversationAsync(second, secondChannel);
        }

        await RemoveMemberAsync(first, Bob);
        await WaitForSubscriptionsAsync(Bob, expected: 1);

        // Alice proves the path works; Bob's clients got nothing from the first organization.
        await SendMessageAsync(NodeA, first, firstChannel, Alice, "after the removal");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("after the removal");
        await CreateChannelAsync(NodeB, first, Alice, "secret-plans");
        (await alice.NextChannelAsync()).Channel.Name.Should().Be("secret-plans");
        bobOnA.HasUnreadMessage.Should().BeFalse();
        bobOnB.HasUnreadMessage.Should().BeFalse();
        bobOnA.HasUnreadChannel.Should().BeFalse();
        bobOnB.HasUnreadChannel.Should().BeFalse();

        // His other organization is untouched, on both nodes.
        await SendMessageAsync(NodeB, second, secondChannel, Bob, "still mine");
        (await bobOnA.NextMessageAsync()).Message.Text.Should().Be("still mine");
        (await bobOnB.NextMessageAsync()).Message.Text.Should().Be("still mine");

        // Joining again needs the membership he no longer has.
        var failure = await Assert.ThrowsAsync<HubException>(() => bobOnB.SubscribeConversationAsync(first, firstChannel));
        failure.Message.Should().EndWith("realtime.not_found");
    }

    [Fact]
    public async Task MemberRemoved_ForAnAccountWithoutConnections_ChangesNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var alice = await ConnectAsync(NodeA, Alice);
        await alice.SubscribeConversationAsync(organizationId, channelId);

        await PublishRemovalAsync(organizationId, await AccountAsync(Bob));

        // A later event proves the listeners have handled the removal (they handle messages in order).
        await PublishRemovalAsync(organizationId, await AccountAsync(Bob));
        await SendMessageAsync(NodeB, organizationId, channelId, Alice, "still here");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("still here");
        (await SubscriptionCountAsync(NodeA, Alice)).Should().Be(1);
    }

    private async Task RemoveMemberAsync(Guid organizationId, string username)
    {
        var account = await AccountAsync(username);
        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, account);
        await PublishRemovalAsync(organizationId, account);
    }

    // What the worker does for the event: its consumer tells every app node through Valkey.
    private async Task PublishRemovalAsync(Guid organizationId, Guid accountId)
    {
        var consumer = new MemberRemovedConsumer(NodeA.Services.GetRequiredService<GroupRemovalBroadcast>());
        await consumer.HandleAsync(new MemberRemoved(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, accountId, 2), CancellationToken);
    }
}
