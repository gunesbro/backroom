using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>The publisher is bounded and never fails its caller, whatever the backplane does.</summary>
public sealed class PublisherTests
{
    private const string SecretText = "text that must never reach a log";

    private static readonly MessagePostedPayload s_message = new(
        Guid.NewGuid(), Guid.NewGuid(), new LiveMessage(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), SecretText, DateTimeOffset.UtcNow));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_WhenTheBackplaneNeverAnswers_GivesUpAfterTheTimeoutAndDoesNotThrowAsync()
    {
        var time = new FakeTimeProvider();
        var logs = new CapturingLoggerProvider();
        using var metrics = new MetricCounter();
        using var publisher = Publisher(new TaskCompletionSource().Task, time, logs, maxConcurrent: 4);

        var publish = publisher.PublishAsync(s_message, CancellationToken);
        publish.IsCompleted.Should().BeFalse("it waits for the backplane until the timeout");
        time.Advance(TimeSpan.FromSeconds(2));
        await publish;

        metrics.Total("backroom.realtime.publish_failures").Should().Be(1);
        logs.Entries.Should().ContainSingle().Which.Should()
            .Contain("MessagePosted").And.Contain(s_message.OrgId.ToString()).And.NotContain(SecretText);
    }

    [Fact]
    public async Task Publish_WhenTheBackplaneFails_DoesNotThrowAndLogsIdsAndTheExceptionTypeOnlyAsync()
    {
        var logs = new CapturingLoggerProvider();
        using var metrics = new MetricCounter();
        using var publisher = Publisher(Task.FromException(new InvalidOperationException(SecretText)), new FakeTimeProvider(), logs, maxConcurrent: 4);

        await publisher.PublishAsync(s_message, CancellationToken);
        await publisher.PublishAsync(new ChannelCreatedPayload(s_message.OrgId, new LiveChannel(Guid.NewGuid(), "name", DateTimeOffset.UtcNow)), CancellationToken);

        metrics.Total("backroom.realtime.publish_failures").Should().Be(2);
        logs.Entries.Should().HaveCount(2);
        logs.Entries.Should().OnlyContain(entry => entry.Contains("InvalidOperationException", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(entry => entry.Contains(SecretText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_WhenTooManyAreInFlight_DropsTheEventInsteadOfQueueingItAsync()
    {
        var time = new FakeTimeProvider();
        var logs = new CapturingLoggerProvider();
        using var metrics = new MetricCounter();
        var stuck = new TaskCompletionSource();
        using var publisher = Publisher(stuck.Task, time, logs, maxConcurrent: 1);
        var first = publisher.PublishAsync(s_message, CancellationToken);

        // The only slot is taken: the next publish returns at once, with its event dropped.
        await publisher.PublishAsync(s_message, CancellationToken);

        metrics.Total("backroom.realtime.publish_failures").Should().Be(1);
        first.IsCompleted.Should().BeFalse();
        logs.Entries.Should().ContainSingle().Which.Should().Contain("dropped");

        // Once the send ends, the slot is free again.
        stuck.SetResult();
        await first;
        metrics.Total("backroom.realtime.publishes").Should().Be(1);
        await publisher.PublishAsync(s_message, CancellationToken);
        metrics.Total("backroom.realtime.publishes").Should().Be(2);
    }

    [Fact]
    public async Task Publish_ToTheRightGroupAndMethod_UsesTheContractNamesAsync()
    {
        var sent = new List<(string Group, string Method)>();
        var hub = new FakeHubContext((group, method) =>
        {
            sent.Add((group, method));
            return Task.CompletedTask;
        });
        using var publisher = new ValkeyLiveEventPublisher(hub, Removals(Options("cellx", 4)), Options("cellx", 4), new FakeTimeProvider(), new CapturingLoggerFactory().CreateLogger<ValkeyLiveEventPublisher>());
        var channel = new ChannelCreatedPayload(s_message.OrgId, new LiveChannel(Guid.NewGuid(), "name", DateTimeOffset.UtcNow));

        await publisher.PublishAsync(s_message, CancellationToken);
        await publisher.PublishAsync(channel, CancellationToken);
        await publisher.PublishAsync(
            new MessagePostedV2Payload(
                s_message.OrgId, s_message.ChannelId, new LiveMessageV2(Guid.NewGuid(), 2, 2, "text", Guid.NewGuid(), Guid.NewGuid(), [], SecretText, DateTimeOffset.UtcNow), 2, "1"),
            CancellationToken);

        sent.Should().Equal(
            ($"cell:cellx:org:{s_message.OrgId:D}:conversation:{s_message.ChannelId:D}", "MessagePosted"),
            ($"cell:cellx:org:{s_message.OrgId:D}", "ChannelCreated"),
            ($"cell:cellx:org:{s_message.OrgId:D}:conversation:{s_message.ChannelId:D}", "MessagePosted.v2"));
    }

    [Fact]
    public async Task RemoveFromConversation_WhenValkeyIsDown_DoesNotThrowAndCountsAFailureAsync()
    {
        var logs = new CapturingLoggerProvider();
        using var metrics = new MetricCounter();
        using var publisher = Publisher(Task.CompletedTask, new FakeTimeProvider(), logs, maxConcurrent: 4);

        await publisher.RemoveFromConversationAsync(s_message.OrgId, s_message.ChannelId, Guid.NewGuid(), CancellationToken);

        metrics.Total("backroom.realtime.publish_failures").Should().Be(1);
        logs.Entries.Should().ContainSingle().Which.Should().Contain("ConversationMemberRemoved").And.Contain(s_message.OrgId.ToString());
    }

    private static ValkeyLiveEventPublisher Publisher(Task send, FakeTimeProvider time, CapturingLoggerProvider logs, int maxConcurrent) =>
        new(
            new FakeHubContext((_, _) => send),
            Removals(Options("cellx", maxConcurrent)),
            Options("cellx", maxConcurrent),
            time,
            new CapturingLoggerFactory(logs).CreateLogger<ValkeyLiveEventPublisher>());

    // Never connected unless a test removes a member; the port refuses, so a removal fails at once.
    private static GroupRemovalBroadcast Removals(IOptions<RealtimeOptions> options) =>
        new(new ValkeyConnection(Microsoft.Extensions.Options.Options.Create(new ValkeyOptions { ConnectionString = "127.0.0.1:1" }), options), options, new FakeTimeProvider());

    private static IOptions<RealtimeOptions> Options(string cellId, int maxConcurrent) =>
        Microsoft.Extensions.Options.Options.Create(new RealtimeOptions { CellId = cellId, MaxConcurrentPublishes = maxConcurrent, PublishTimeout = TimeSpan.FromSeconds(2) });

    private sealed class CapturingLoggerFactory(CapturingLoggerProvider? provider = null) : ILoggerFactory
    {
        private readonly CapturingLoggerProvider _provider = provider ?? new CapturingLoggerProvider();

        public void AddProvider(ILoggerProvider loggerProvider) => throw new NotSupportedException();

        public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A node whose Valkey does not exist: saving a message must still work. The revocation check is set to fail open here, so the
/// test isolates the publisher (the default fail-closed behavior is covered by <see cref="RevocationOutageTests"/>).
/// </summary>
[Collection(CellCollection.Name)]
public sealed class PublishFailureTests(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey)
    : RealtimeTestBase(database, keycloak, valkey)
{
    [Fact]
    public async Task SendMessage_WhenValkeyIsDown_StillStoresTheMessageAndAnswersCreatedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(NodeA, organizationId, Alice, "general");
        await using var broken = await CellNode.StartAsync(
            Database,
            Keycloak.Url,
            CellId,
            "127.0.0.1:1",
            settings: new Dictionary<string, string?> { ["Realtime:RevocationFailClosed"] = "false" },
            cancellationToken: CancellationToken);
        using var metrics = new MetricCounter();

        // The first send warms up the node (JIT, connections); the second is the one that must be fast.
        using var warmUp = await PostAsync(
            broken, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice, new { clientMessageId = Guid.NewGuid(), text = "warm up" });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await PostAsync(
            broken, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice, new { clientMessageId = Guid.NewGuid(), text = "kept anyway" });

        stopwatch.Stop();
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "a down Valkey must not slow the send request");
        metrics.Total("backroom.realtime.publish_failures").Should().BeGreaterThanOrEqualTo(1);
        broken.Logs.Entries.Should().Contain(entry => entry.Contains("Publishing the live event MessagePosted", StringComparison.Ordinal));
        broken.Logs.Entries.Should().NotContain(entry => entry.Contains("kept anyway", StringComparison.Ordinal));
    }
}
