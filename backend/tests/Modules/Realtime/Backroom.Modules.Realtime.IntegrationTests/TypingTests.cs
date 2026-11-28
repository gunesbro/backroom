using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// The hub's <c>Typing</c> (docs/contracts/phase-2.md, flow 2 "Typing"): a person who may post tells the conversation's group,
/// except their own connection, at most once per 3 seconds per conversation; never stored; every refusal is <c>realtime.not_found</c>.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class TypingTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    private const string NotFound = "realtime.not_found";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    protected override IReadOnlyDictionary<string, string?>? Settings => new Dictionary<string, string?> { ["Features:Typing"] = "true" };

    protected override TimeProvider? Time => _time;

    [Fact]
    public async Task Typing_ByAMemberWhoMayPost_ReachesTheOtherSubscribersButNotTheCallerAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        await JoinAsync(NodeA, organizationId, channelId, Bob);
        var alice = await ConnectAsync(NodeA, Alice);
        var bobOnB = await ConnectAsync(NodeB, Bob);
        await alice.SubscribeConversationAsync(organizationId, channelId);
        await bobOnB.SubscribeConversationAsync(organizationId, channelId);

        await alice.TypingAsync(organizationId, channelId);

        var typing = await bobOnB.NextTypingAsync();
        typing.Should().Be(new UserTypingPayload(organizationId, channelId, null, await AccountAsync(Alice)));
        // A later event on the same group proves the path, so Alice's silence is not just slowness.
        await SendMessageAsync(NodeA, organizationId, channelId, Bob, "after");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("after");
        alice.HasUnreadTyping.Should().BeFalse("the sender's own connection is left out");
    }

    [Fact]
    public async Task Typing_Repeated_DeliversOneEventPerIntervalAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        await JoinAsync(NodeA, organizationId, channelId, Bob);
        var alice = await ConnectAsync(NodeA, Alice);
        var bob = await ConnectAsync(NodeB, Bob);
        await bob.SubscribeConversationAsync(organizationId, channelId);

        await alice.TypingAsync(organizationId, channelId);
        (await bob.NextTypingAsync()).AccountId.Should().Be(await AccountAsync(Alice));
        await alice.TypingAsync(organizationId, channelId);
        _time.Advance(TimeSpan.FromSeconds(2.9));
        await alice.TypingAsync(organizationId, channelId);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        await alice.TypingAsync(organizationId, channelId);

        // Exactly the first and the one after the interval: were an earlier call delivered, the last would still be unread.
        (await bob.NextTypingAsync()).ConversationId.Should().Be(channelId);
        await SendMessageAsync(NodeA, organizationId, channelId, Alice, "marker");
        (await bob.NextMessageAsync()).Message.Text.Should().Be("marker");
        bob.HasUnreadTyping.Should().BeFalse();
    }

    [Fact]
    public async Task Typing_FromTwoConnectionsOfOneAccount_IsThrottledPerConversationAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var first = await CreateChannelAsync(NodeA, organizationId, Alice, "one");
        var second = await CreateChannelAsync(NodeA, organizationId, Alice, "two");
        var alice = await ConnectAsync(NodeA, Alice);
        var aliceAgain = await ConnectAsync(NodeA, Alice);
        var watcher = await ConnectAsync(NodeB, Alice);
        await watcher.SubscribeConversationAsync(organizationId, first);
        await watcher.SubscribeConversationAsync(organizationId, second);

        await alice.TypingAsync(organizationId, first);
        await aliceAgain.TypingAsync(organizationId, first);
        await aliceAgain.TypingAsync(organizationId, second);

        var received = new[] { (await watcher.NextTypingAsync()).ConversationId, (await watcher.NextTypingAsync()).ConversationId };
        received.Should().BeEquivalentTo([first, second]);
        await SendMessageAsync(NodeA, organizationId, second, Alice, "marker");
        (await watcher.NextMessageAsync()).Message.Text.Should().Be("marker");
        watcher.HasUnreadTyping.Should().BeFalse("the second call for the first conversation, from another connection, was dropped");
    }

    [Fact]
    public async Task Typing_WhereThePersonMayNotPost_IsRefusedAsNotFoundAndNothingIsDeliveredAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var otherOrganization = await ProvisionForAsync(Bob);
        var publicId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var archived = await CreateChannelAsync(NodeA, organizationId, Alice, "old");
        await JoinAsync(NodeA, organizationId, archived, Bob);
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", archived);
        var secret = await CreatePrivateConversationAsync(organizationId, Alice);
        var alice = await ConnectAsync(NodeA, Alice);
        await alice.SubscribeConversationAsync(organizationId, publicId);
        await alice.SubscribeConversationAsync(organizationId, secret);
        var bob = await ConnectAsync(NodeB, Bob);

        var refusals = new[]
        {
            await Assert.ThrowsAsync<HubException>(() => bob.TypingAsync(organizationId, publicId)),
            await Assert.ThrowsAsync<HubException>(() => bob.TypingAsync(organizationId, archived)),
            await Assert.ThrowsAsync<HubException>(() => bob.TypingAsync(organizationId, secret)),
            await Assert.ThrowsAsync<HubException>(() => bob.TypingAsync(organizationId, Guid.NewGuid())),
            await Assert.ThrowsAsync<HubException>(() => bob.TypingAsync(otherOrganization, secret)),
        };

        refusals.Should().OnlyContain(failure => failure.Message.EndsWith(NotFound, StringComparison.Ordinal));
        await SendMessageAsync(NodeA, organizationId, publicId, Alice, "marker");
        (await alice.NextMessageAsync()).Message.Text.Should().Be("marker");
        alice.HasUnreadTyping.Should().BeFalse();
    }

    [Fact]
    public async Task Typing_InAnArchivedConversationOfAMember_IsRefusedAsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "old");
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", channelId);
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.TypingAsync(organizationId, channelId));

        failure.Message.Should().EndWith(NotFound, "an archived conversation is read-only");
    }

    [Fact]
    public async Task Typing_WithAThreadId_IsRefusedAsNotFoundUntilThreadsShipAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.TypingAsync(organizationId, channelId, Guid.NewGuid()));

        failure.Message.Should().EndWith(NotFound);
    }

    [Fact]
    public async Task Typing_WithAnEmptyId_IsInvalidAsync()
    {
        var alice = await ConnectAsync(NodeA, Alice);

        var failure = await Assert.ThrowsAsync<HubException>(() => alice.TypingAsync(Guid.Empty, Guid.NewGuid()));

        failure.Message.Should().EndWith("realtime.invalid_argument");
    }

    [Fact]
    public void Throttle_WhenFull_SweepsExpiredEntriesAndThenDropsCalls()
    {
        var options = new RealtimeOptions { MaxTypingEntries = 16 };
        var throttle = new TypingThrottle(_time, options);
        var account = Guid.NewGuid();
        for (var i = 0; i < 16; i++)
        {
            throttle.TryAcquire(account, Guid.NewGuid()).Should().BeTrue();
        }

        throttle.TryAcquire(account, Guid.NewGuid()).Should().BeFalse("a full table drops the call");
        _time.Advance(options.TypingInterval);
        throttle.TryAcquire(account, Guid.NewGuid()).Should().BeTrue("expired entries are swept");
    }
}
