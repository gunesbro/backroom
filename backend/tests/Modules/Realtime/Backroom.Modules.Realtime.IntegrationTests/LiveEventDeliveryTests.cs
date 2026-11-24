using AwesomeAssertions;
using MessagePack;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// Live events from one app node to clients on another, through the Valkey backplane (docs/contracts/phase-1.md,
/// flow 4, steps 4 and 5).
/// </summary>
[Collection(CellCollection.Name)]
public sealed class LiveEventDeliveryTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task MessagePosted_PostedThroughNodeA_ReachesAClientConnectedToNodeBAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var bob = await ConnectAsync(NodeB, Bob);
        await bob.SubscribeChannelAsync(organizationId, channelId);

        var sent = await SendMessageAsync(NodeA, organizationId, channelId, Alice, "Hello <b>there</b> & \"you\"");

        var received = await bob.NextMessageAsync();
        received.OrgId.Should().Be(organizationId);
        received.ChannelId.Should().Be(channelId);
        received.Message.Id.Should().Be(sent["id"]!.GetValue<Guid>());
        received.Message.Seq.Should().Be(1);
        received.Message.ClientMessageId.Should().Be(sent["clientMessageId"]!.GetValue<Guid>());
        received.Message.AuthorAccountId.Should().Be(await AccountAsync(Alice));
        received.Message.Text.Should().Be("Hello <b>there</b> & \"you\"");
        received.Message.CreatedAt.Should().Be(sent["createdAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task MessagePosted_WithClientsOnBothNodes_ReachesEverySubscriberOnceAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var aliceOnA = await ConnectAsync(NodeA, Alice);
        var bobOnB = await ConnectAsync(NodeB, Bob);
        await aliceOnA.SubscribeChannelAsync(organizationId, channelId);
        await bobOnB.SubscribeChannelAsync(organizationId, channelId);

        await SendMessageAsync(NodeB, organizationId, channelId, Bob, "first");
        await SendMessageAsync(NodeB, organizationId, channelId, Bob, "second");

        (await aliceOnA.NextMessageAsync()).Message.Text.Should().Be("first");
        (await aliceOnA.NextMessageAsync()).Message.Text.Should().Be("second");
        (await bobOnB.NextMessageAsync()).Message.Text.Should().Be("first");
        (await bobOnB.NextMessageAsync()).Message.Text.Should().Be("second");
        aliceOnA.HasUnreadMessage.Should().BeFalse();
        bobOnB.HasUnreadMessage.Should().BeFalse();
    }

    [Fact]
    public async Task ChannelCreated_WithAnOrganizationSubscriberOnAnotherNode_ReachesItWithoutContentAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await ConnectAsync(NodeB, Bob);
        await bob.SubscribeOrganizationAsync(organizationId);

        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "announcements");

        var received = await bob.NextChannelAsync();
        received.OrgId.Should().Be(organizationId);
        received.Channel.Id.Should().Be(channelId);
        received.Channel.Name.Should().Be("announcements");
    }

    [Fact]
    public async Task MessagePosted_InAnotherChannelOrAfterUnsubscribing_IsNotDeliveredAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var general = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var random = await CreateChannelAsync(NodeA, organizationId, Alice, "random");
        var bob = await ConnectAsync(NodeB, Bob);
        var alice = await ConnectAsync(NodeB, Alice);
        await bob.SubscribeChannelAsync(organizationId, general);
        await alice.SubscribeChannelAsync(organizationId, general);
        await alice.SubscribeChannelAsync(organizationId, random);

        await SendMessageAsync(NodeA, organizationId, random, Alice, "in random");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("in random");
        bob.HasUnreadMessage.Should().BeFalse("Bob never joined that channel");

        await bob.UnsubscribeChannelAsync(organizationId, general);
        await SendMessageAsync(NodeA, organizationId, general, Alice, "in general");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("in general");
        bob.HasUnreadMessage.Should().BeFalse("Bob left that channel");
    }

    [Fact]
    public void LivePayloads_Serialized_UseCamelCaseKeysAsTheContractListsThem()
    {
        var payload = new MessagePostedPayload(
            Guid.NewGuid(), Guid.NewGuid(), new LiveMessage(Guid.NewGuid(), 7, Guid.NewGuid(), Guid.NewGuid(), "x", DateTimeOffset.UtcNow));

        var cancellationToken = TestContext.Current.CancellationToken;
        var json = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(payload, cancellationToken: cancellationToken), cancellationToken: cancellationToken);

        json.Should().Contain("\"orgId\"").And.Contain("\"channelId\"").And.Contain("\"message\"")
            .And.Contain("\"id\"").And.Contain("\"seq\"").And.Contain("\"clientMessageId\"")
            .And.Contain("\"authorAccountId\"").And.Contain("\"text\"").And.Contain("\"createdAt\"");
    }
}
