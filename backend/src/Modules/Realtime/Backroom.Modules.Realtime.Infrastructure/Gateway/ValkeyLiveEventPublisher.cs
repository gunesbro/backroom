using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Sends live events to the SignalR group; the Valkey backplane carries them to the connections on every app node.
/// A publish is bounded in every way: it waits at most <see cref="RealtimeOptions.PublishTimeout"/>, at most
/// <see cref="RealtimeOptions.MaxConcurrentPublishes"/> run at once (further events are dropped, not queued), and a
/// failure is logged with ids and the exception type only and never reaches the caller. Live events are best effort;
/// clients catch up with a read after reconnecting.
/// </summary>
internal sealed partial class ValkeyLiveEventPublisher : ILiveEventPublisher, IDisposable
{
    // The metric and log name of the group removal; it is not a client event.
    private const string RemovalEventName = "ConversationMemberRemoved";

    private readonly IHubContext<RealtimeHub> _hub;
    private readonly RealtimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ValkeyLiveEventPublisher> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly GroupRemovalBroadcast _removals;

    public ValkeyLiveEventPublisher(
        IHubContext<RealtimeHub> hub,
        GroupRemovalBroadcast removals,
        IOptions<RealtimeOptions> options,
        TimeProvider timeProvider,
        ILogger<ValkeyLiveEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hub = hub;
        _removals = removals;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _slots = new SemaphoreSlim(_options.MaxConcurrentPublishes, _options.MaxConcurrentPublishes);
    }

    public Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(
            LiveEventNames.MessagePosted, GroupNames.Conversation(_options.CellId, payload.OrgId, payload.ChannelId), payload, payload.OrgId, cancellationToken);
    }

    public Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(
            LiveEventNames.ChannelCreated, GroupNames.Organization(_options.CellId, payload.OrgId), payload, payload.OrgId, cancellationToken);
    }

    public Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(
            LiveEventNames.PresenceChanged, GroupNames.Presence(_options.CellId, payload.OrgId, payload.AccountId), payload, payload.OrgId, cancellationToken);
    }

    public Task PublishAsync(NotificationRequestedPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(
            LiveEventNames.NotificationRequested, GroupNames.Account(_options.CellId, payload.OrgId, payload.AccountId), payload, payload.OrgId, cancellationToken);
    }

    public async Task RemoveFromConversationAsync(Guid orgId, Guid conversationId, Guid accountId, CancellationToken cancellationToken)
    {
        // Bounded like a publish (the request waits for it); the consumer of the durable event repeats it when this is lost.
        try
        {
            await _removals.PublishConversationAsync(orgId, conversationId, accountId, _options.PublishTimeout, cancellationToken);
        }
#pragma warning disable CA1031 // Best effort: whatever Valkey throws must not reach the caller.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RealtimeTelemetry.PublishFailures.Add(
                1, new KeyValuePair<string, object?>("event", RemovalEventName), new KeyValuePair<string, object?>("reason", "error"));
            LogFailed(_logger, RemovalEventName, exception.GetType().Name, orgId);
        }
    }

    public void Dispose() => _slots.Dispose();

    private async Task PublishAsync(string eventName, string group, object payload, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!_slots.Wait(0, CancellationToken.None))
        {
            RealtimeTelemetry.PublishFailures.Add(
                1, new KeyValuePair<string, object?>("event", eventName), new KeyValuePair<string, object?>("reason", "saturated"));
            LogDropped(_logger, eventName, organizationId);
            return;
        }

        // The slot is released when the send ends, not when the caller stops waiting, so a stuck Valkey cannot pile up sends.
        var attempt = new Attempt();
        var send = SendAsync(attempt, eventName, group, payload, organizationId);
        try
        {
            await send.WaitAsync(_options.PublishTimeout, _timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Counted here; if the send fails later, it is not counted again.
            if (attempt.TryReport())
            {
                RealtimeTelemetry.PublishFailures.Add(
                    1, new KeyValuePair<string, object?>("event", eventName), new KeyValuePair<string, object?>("reason", "timeout"));
                LogTimedOut(_logger, eventName, organizationId);
            }
        }
        catch (OperationCanceledException)
        {
            if (attempt.TryReport())
            {
                RealtimeTelemetry.PublishFailures.Add(
                    1, new KeyValuePair<string, object?>("event", eventName), new KeyValuePair<string, object?>("reason", "cancelled"));
            }
        }
    }

    // Never throws, so a send that outlives its caller faults nobody. Counts its own outcome unless the caller already did.
    private async Task SendAsync(Attempt attempt, string eventName, string group, object payload, Guid organizationId)
    {
        var tag = new KeyValuePair<string, object?>("event", eventName);
        try
        {
            await _hub.Clients.Group(group).SendAsync(eventName, payload, CancellationToken.None);
            if (attempt.TryReport())
            {
                RealtimeTelemetry.Publishes.Add(1, tag);
            }
        }
#pragma warning disable CA1031 // A live event is best effort: whatever the backplane throws must not reach the caller.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (attempt.TryReport())
            {
                RealtimeTelemetry.PublishFailures.Add(1, tag, new KeyValuePair<string, object?>("reason", "error"));
                LogFailed(_logger, eventName, exception.GetType().Name, organizationId);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Whether the outcome of one publish has been counted: the caller (timeout) or the send, whichever is first.</summary>
    private sealed class Attempt
    {
        private int _reported;

        public bool TryReport() => Interlocked.Exchange(ref _reported, 1) == 0;
    }

    // The exception type only: its message could carry the payload.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Publishing the live event {EventName} failed ({ExceptionType}) in organization {OrganizationId}.")]
    private static partial void LogFailed(ILogger logger, string eventName, string exceptionType, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Publishing the live event {EventName} timed out in organization {OrganizationId}.")]
    private static partial void LogTimedOut(ILogger logger, string eventName, Guid organizationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The live event {EventName} was dropped in organization {OrganizationId}: too many publishes in flight.")]
    private static partial void LogDropped(ILogger logger, string eventName, Guid organizationId);
}
