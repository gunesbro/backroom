using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Presence.Contracts;
using Backroom.Modules.Presence.Infrastructure;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// Presence (docs/contracts/phase-2.md, flow 9) on two app nodes with one Valkey and a fake clock: online from a hub connection, idle
/// after 10 minutes without input or when the client says so, offline 60 seconds after the last connection; events only to the
/// presence groups of people who watch in an organization they share (owner decision 16), at most one per person per 5 seconds,
/// coalesced; the status copy from the link in the events and the read endpoint, never the time zone.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class PresenceTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    protected override IReadOnlyDictionary<string, string?>? Settings => new Dictionary<string, string?> { ["Features:Presence"] = "true" };

    protected override TimeProvider? Time => _time;

    [Fact]
    public async Task Connect_OfAMember_ReachesWatchersInTheSameOrganizationOnAnotherNodeAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);

        await ConnectAsync(NodeA, Alice);

        var change = await bob.NextPresenceAsync();
        change.OrgId.Should().Be(organizationId);
        change.AccountId.Should().Be(alice);
        change.State.Should().Be("online");
        change.DndUntil.Should().BeNull();
        change.CustomStatus.Should().BeNull();
    }

    [Fact]
    public async Task Idle_AfterTenMinutesWithoutInput_IsPublishedAndInputMakesThePersonOnlineAgainAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        var aliceClient = await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");

        // Ten minutes pass without input. Both nodes sweep every step, as their timers would: Alice's node keeps her connection
        // alive, and Bob's node renews his watch at half its lifetime; without that the watch would end at minute 10, exactly when
        // the idle event is published, and the event would reach nobody (the race that made this test depend on the timers).
        for (var step = 0; step < 30; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await SweepAsync(NodeA);
            await SweepAsync(NodeB);
        }

        (await bob.NextPresenceAsync()).State.Should().Be("idle");

        // Input right after the idle event: the change waits for the 5 seconds between events, then goes out.
        await aliceClient.ReportActivityAsync("active");
        _time.Advance(TimeSpan.FromSeconds(5));
        await SweepAsync(NodeB);
        await SweepAsync(NodeA);

        (await bob.NextPresenceAsync()).State.Should().Be("online");
    }

    [Fact]
    public async Task ReportActivity_Idle_IsPublishedAtOnceAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        var aliceClient = await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");
        _time.Advance(TimeSpan.FromSeconds(10));

        await aliceClient.ReportActivityAsync("idle");

        (await bob.NextPresenceAsync()).State.Should().Be("idle");
    }

    [Fact]
    public async Task ReportActivity_MoreThanOnceInTenSeconds_DropsTheExtraReportAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        var aliceClient = await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");
        _time.Advance(TimeSpan.FromSeconds(10));
        await aliceClient.ReportActivityAsync("idle");
        (await bob.NextPresenceAsync()).State.Should().Be("idle");

        _time.Advance(TimeSpan.FromSeconds(6));
        await aliceClient.ReportActivityAsync("active");
        await SweepAsync(NodeA);
        bob.HasUnreadPresence.Should().BeFalse("a report less than 10 seconds after the last one is dropped");

        _time.Advance(TimeSpan.FromSeconds(4));
        await aliceClient.ReportActivityAsync("active");

        (await bob.NextPresenceAsync()).State.Should().Be("online");
    }

    [Fact]
    public async Task Offline_SixtySecondsAfterTheLastConnection_IsPublishedAndNotBeforeAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        var aliceClient = await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");

        await aliceClient.DisposeAsync();
        await WaitUntilAsync(() => Task.FromResult(NodeA.Services.GetRequiredService<PresenceTracker>().Snapshot().Count == 0));
        _time.Advance(TimeSpan.FromSeconds(59));
        await SweepAsync(NodeA);
        await SweepAsync(NodeB);
        (await GetPresenceStateAsync(organizationId, alice)).Should().Be("online", "a reload must not look like leaving");
        bob.HasUnreadPresence.Should().BeFalse();

        _time.Advance(TimeSpan.FromSeconds(2));
        await SweepAsync(NodeB);

        (await bob.NextPresenceAsync()).State.Should().Be("offline");
    }

    [Fact]
    public async Task Changes_WithinFiveSeconds_AreCoalescedIntoOneEventWithTheLatestStatusAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");
        var sink = NodeA.Services.GetRequiredService<IAccountStatusSink>();
        var now = _time.GetUtcNow();

        await sink.ApplyAsync(new AccountStatusCopy(alice, now.AddMinutes(30), null, "First", null, "Europe/Istanbul", 1), CancellationToken);
        await SweepAsync(NodeA);
        _time.Advance(TimeSpan.FromSeconds(1));
        await sink.ApplyAsync(new AccountStatusCopy(alice, now.AddHours(1), "🎯", "Second", now.AddHours(2), "Europe/Istanbul", 2), CancellationToken);
        await SweepAsync(NodeA);
        await SweepAsync(NodeB);
        bob.HasUnreadPresence.Should().BeFalse("the person had an event less than 5 seconds ago");

        _time.Advance(TimeSpan.FromSeconds(4));
        await SweepAsync(NodeB);

        var change = await bob.NextPresenceAsync();
        change.State.Should().Be("online");
        change.DndUntil.Should().Be(PresenceRules.Iso(now.AddHours(1)));
        change.CustomStatus.Should().BeEquivalentTo(new LiveCustomStatus("🎯", "Second", PresenceRules.Iso(now.AddHours(2))));
        _time.Advance(TimeSpan.FromSeconds(10));
        await SweepAsync(NodeA);
        await SweepAsync(NodeB);
        bob.HasUnreadPresence.Should().BeFalse("the first status was coalesced away and nothing changed since");
    }

    [Fact]
    public async Task ApplyStatus_WithAnOlderOrTheSameVersion_ChangesNothingAsync()
    {
        var account = Guid.NewGuid();
        var sink = NodeA.Services.GetRequiredService<IAccountStatusSink>();
        var now = _time.GetUtcNow();

        (await sink.ApplyAsync(new AccountStatusCopy(account, null, null, "Two", null, "UTC", 2), CancellationToken)).Should().BeTrue();
        (await sink.ApplyAsync(new AccountStatusCopy(account, null, null, "One", null, "UTC", 1), CancellationToken)).Should().BeFalse();
        (await sink.ApplyAsync(new AccountStatusCopy(account, now.AddHours(1), null, "Two again", null, "UTC", 2), CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task GetPresence_ReturnsStateAndStatusOfMembersOnlyAndNeverTheTimeZoneAsync()
    {
        var (organizationId, alice, bobAccount) = await OrganizationOfTwoAsync();
        await ConnectAsync(NodeA, Alice);
        var now = _time.GetUtcNow();
        await NodeA.Services.GetRequiredService<IAccountStatusSink>().ApplyAsync(
            new AccountStatusCopy(alice, now.AddMinutes(30), "🌴", "Away", null, "Europe/Istanbul", 1), CancellationToken);
        var stranger = Guid.NewGuid();
        await NodeA.Services.GetRequiredService<IAccountStatusSink>().ApplyAsync(
            new AccountStatusCopy(stranger, now.AddMinutes(30), null, "Not here", null, "UTC", 1), CancellationToken);

        using var response = await GetAsync(NodeB, $"/api/v1/orgs/{organizationId}/presence?accountIds={alice},{bobAccount},{stranger}", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await response.Content.ReadAsStringAsync(CancellationToken);
        text.Should().NotContain("Europe/Istanbul").And.NotContain("timeZone").And.NotContain(stranger.ToString(), "a non-member is left out");
        var people = JsonNode.Parse(text)!["presence"]!.AsArray();
        people.Should().HaveCount(2);
        var aliceEntry = people.Single(person => person!["accountId"]!.GetValue<Guid>() == alice)!;
        aliceEntry["state"]!.GetValue<string>().Should().Be("online");
        aliceEntry["dndUntil"]!.GetValue<DateTimeOffset>().Should().BeCloseTo(now.AddMinutes(30), TimeSpan.FromMilliseconds(1));
        aliceEntry["customStatus"]!["emoji"]!.GetValue<string>().Should().Be("🌴");
        people.Single(person => person!["accountId"]!.GetValue<Guid>() == bobAccount)!["state"]!.GetValue<string>().Should().Be("offline");
    }

    [Fact]
    public async Task Presence_IsNeverVisibleThroughAnOrganizationThePersonIsNotIn_Async()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bobsOwn = await ProvisionForAsync(Bob);
        var bob = await WatchingAsync(NodeB, Bob, bobsOwn, alice);
        await ConnectAsync(NodeA, Alice);

        using var response = await GetAsync(NodeB, $"/api/v1/orgs/{bobsOwn}/presence?accountIds={alice}", Bob);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!["presence"]!.AsArray().Should().BeEmpty(
            "Alice is not a member of Bob's own organization");

        // A watcher in the shared organization proves the event was published; Bob's watch in his own organization got nothing.
        var watcher = await WatchingAsync(NodeA, Bob, organizationId, alice);
        await NodeA.Services.GetRequiredService<IAccountStatusSink>().ApplyAsync(
            new AccountStatusCopy(alice, null, null, "Hi", null, "UTC", 1), CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(5));
        await SweepAsync(NodeA);
        (await watcher.NextPresenceAsync()).OrgId.Should().Be(organizationId);
        bob.HasUnreadPresence.Should().BeFalse();
    }

    [Fact]
    public async Task GetPresence_MoreThan120TimesAMinute_Answers429Async()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        for (var i = 0; i < 120; i++)
        {
            using var accepted = await GetAsync(NodeB, $"/api/v1/orgs/{organizationId}/presence?accountIds={alice}", Bob);
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var response = await GetAsync(NodeB, $"/api/v1/orgs/{organizationId}/presence?accountIds={alice}", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!["code"]!.GetValue<string>().Should().Be("presence.rate_limited");
    }

    [Fact]
    public async Task WatchPresence_InAnOrganizationOfWhichTheCallerIsNotAMember_IsRefusedAsNotFoundAsync()
    {
        var (_, alice, _) = await OrganizationOfTwoAsync();
        var alicesOther = await ProvisionForAsync(Alice);
        var bob = await ConnectAsync(NodeB, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.WatchPresenceAsync(alicesOther, alice));

        failure.Message.Should().EndWith("realtime.not_found");
    }

    [Fact]
    public async Task WatchPresence_WithMoreThan200People_IsRefusedAsInvalidAsync()
    {
        var (organizationId, _, _) = await OrganizationOfTwoAsync();
        var bob = await ConnectAsync(NodeB, Bob);

        var failure = await Assert.ThrowsAsync<HubException>(() => bob.WatchPresenceAsync(organizationId, [.. Enumerable.Range(0, 201).Select(_ => Guid.NewGuid())]));

        failure.Message.Should().EndWith("realtime.invalid_argument");
    }

    [Fact]
    public async Task ForgetMembership_StopsThePersonsPresenceFromReachingTheOrganizationAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");

        await NodeA.Services.GetRequiredService<IPresenceMemberships>().ForgetAsync(organizationId, alice, CancellationToken);
        await NodeA.Services.GetRequiredService<IAccountStatusSink>().ApplyAsync(
            new AccountStatusCopy(alice, null, null, "Gone", null, "UTC", 1), CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(5));
        await SweepAsync(NodeA);
        await SweepAsync(NodeB);

        bob.HasUnreadPresence.Should().BeFalse();
    }

    [Fact]
    public async Task RenewingAWatch_AfterThePersonLeftTheOrganization_DoesNotBringThemBackAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        await ConnectAsync(NodeA, Alice);
        (await bob.NextPresenceAsync()).State.Should().Be("online");

        // The worker forgot the watch, but the broadcast to the app nodes was lost: Bob's node still holds the watch set.
        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, alice);
        await NodeA.Services.GetRequiredService<IPresenceMemberships>().ForgetAsync(organizationId, alice, CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await SweepAsync(NodeB);
        await SweepAsync(NodeA);

        await NodeA.Services.GetRequiredService<IAccountStatusSink>().ApplyAsync(
            new AccountStatusCopy(alice, null, null, "Still here?", null, "UTC", 1), CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(5));
        await SweepAsync(NodeA);
        await SweepAsync(NodeB);

        bob.HasUnreadPresence.Should().BeFalse("the renewal checks the membership again and drops the person from the watch");
    }

    [Fact]
    public async Task MemberRemoval_DropsThePersonsPresenceGroupFromWatchersOnEveryNodeAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        await WatchingAsync(NodeB, Bob, organizationId, alice);
        await WatchingAsync(NodeA, Bob, organizationId, alice);
        var group = $"cell:{CellId}:org:{organizationId:D}:presence:{alice:D}";

        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, alice);
        var consumer = new Backroom.Modules.Realtime.Infrastructure.Gateway.MemberRemovedConsumer(
            NodeA.Services.GetRequiredService<Backroom.Modules.Realtime.Infrastructure.Gateway.GroupRemovalBroadcast>(),
            NodeA.Services.GetRequiredService<IPresenceMemberships>());
        await consumer.HandleAsync(
            new Backroom.Modules.Organization.Contracts.MemberRemoved(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, alice, 2), CancellationToken);

        foreach (var node in new[] { NodeA, NodeB })
        {
            var registry = node.Services.GetRequiredService<Backroom.Modules.Realtime.Infrastructure.Gateway.ConnectionRegistry>();
            await WaitUntilAsync(() => Task.FromResult(registry.All().All(state => !state.Watched.Contains(group))));
            node.Services.GetRequiredService<PresenceTracker>().TakeWatchesToRenew(DateTimeOffset.MaxValue, _time.GetUtcNow())
                .Should().OnlyContain(watch => !watch.AccountIds.Contains(alice), "the node stops renewing the watch");
        }
    }

    [Fact]
    public async Task MemberRemoval_OfAWatcher_LeavesTheOrganizationAccountConversationAndPresenceGroupsTogetherAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var general = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        var bob = await ConnectAsync(NodeB, Bob);
        await bob.SubscribeOrganizationAsync(organizationId);
        await bob.SubscribeConversationAsync(organizationId, general);
        await bob.WatchPresenceAsync(organizationId, alice);
        (await SubscriptionCountAsync(NodeB, Bob)).Should().Be(4, "organization, account, conversation and presence groups");

        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob));
        await new Backroom.Modules.Realtime.Infrastructure.Gateway.MemberRemovedConsumer(
            NodeA.Services.GetRequiredService<Backroom.Modules.Realtime.Infrastructure.Gateway.GroupRemovalBroadcast>(),
            NodeA.Services.GetRequiredService<IPresenceMemberships>())
            .HandleAsync(new Backroom.Modules.Organization.Contracts.MemberRemoved(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, await AccountAsync(Bob), 2), CancellationToken);

        await WaitForSubscriptionsAsync(Bob, expected: 0);
    }

    [Fact]
    public async Task Logs_OfPresenceAndStatus_CarryNoStatusTextEmojiOrTimeZoneAsync()
    {
        var (organizationId, alice, _) = await OrganizationOfTwoAsync();
        var bob = await WatchingAsync(NodeB, Bob, organizationId, alice);
        var aliceClient = await ConnectAsync(NodeA, Alice);
        await NodeA.Services.GetRequiredService<IAccountStatusSink>().ApplyAsync(
            new AccountStatusCopy(alice, null, "🦄", "SecretStatusText", null, "Pacific/Chatham", 1), CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));
        await aliceClient.ReportActivityAsync("idle");
        await SweepAsync(NodeA);
        await SweepAsync(NodeB);
        await bob.NextPresenceAsync();
        using var read = await GetAsync(NodeB, $"/api/v1/orgs/{organizationId}/presence?accountIds={alice}", Bob);

        foreach (var node in new[] { NodeA, NodeB })
        {
            node.Logs.Entries.Should().NotContain(line => line.Contains("SecretStatusText", StringComparison.Ordinal)
                || line.Contains("🦄", StringComparison.Ordinal)
                || line.Contains("Pacific/Chatham", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PresenceChanged_CarriesNoPlatformDeviceOrTimeZone()
    {
        var keys = typeof(PresenceChangedPayload).GetProperties().Select(property => property.Name)
            .Concat(typeof(LiveCustomStatus).GetProperties().Select(property => property.Name));

        keys.Should().BeEquivalentTo(["OrgId", "AccountId", "State", "DndUntil", "CustomStatus", "Emoji", "Text", "ExpiresAt"]);
    }

    private async Task<(Guid OrganizationId, Guid Alice, Guid Bob)> OrganizationOfTwoAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        return (organizationId, await AccountAsync(Alice), await AccountAsync(Bob));
    }

    private async Task<HubClient> WatchingAsync(CellNode node, string username, Guid organizationId, params Guid[] accountIds)
    {
        var client = await ConnectAsync(node, username);
        await client.WatchPresenceAsync(organizationId, accountIds);
        return client;
    }

    private async Task<string> GetPresenceStateAsync(Guid organizationId, Guid accountId)
    {
        using var response = await GetAsync(NodeB, $"/api/v1/orgs/{organizationId}/presence?accountIds={accountId}", Bob);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!["presence"]![0]!["state"]!.GetValue<string>();
    }

    private static Task SweepAsync(CellNode node) =>
        node.Services.GetServices<IHostedService>().OfType<PresenceSweeper>().Single().RunOnceAsync(CancellationToken);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(HubClient.Deadline);
        while (!await condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
