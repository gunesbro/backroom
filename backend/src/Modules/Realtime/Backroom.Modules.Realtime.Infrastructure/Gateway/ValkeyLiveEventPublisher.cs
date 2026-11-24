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
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly RealtimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ValkeyLiveEventPublisher> _logger;
    private readonly SemaphoreSlim _slots;

    public ValkeyLiveEventPublisher(
        IHubContext<RealtimeHub> hub,
        IOptions<RealtimeOptions> options,
        TimeProvider timeProvider,
        ILogger<ValkeyLiveEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hub = hub;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _slots = new SemaphoreSlim(_options.MaxConcurrentPublishes, _options.MaxConcurrentPublishes);
    }

    public Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(
            LiveEventNames.MessagePosted, GroupNames.Channel(_options.CellId, payload.OrgId, payload.ChannelId), payload, payload.OrgId, cancellationToken);
    }

    public Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(
            LiveEventNames.ChannelCreated, GroupNames.Organization(_options.CellId, payload.OrgId), payload, payload.OrgId, cancellationToken);
    }

    public void Dispose() => _slots.Dispose();

    private async Task PublishAsync(string eventName, string group, object payload, Guid organizationId, CancellationToken cancellationToken)
    {
        var tag = new KeyValuePair<string, object?>("event", eventName);
        if (!_slots.Wait(0, CancellationToken.None))
        {
            RealtimeTelemetry.PublishFailures.Add(1, tag, new KeyValuePair<string, object?>("reason", "saturated"));
            LogDropped(_logger, eventName, organizationId);
            return;
        }

        // The slot is released when the send ends, not when the caller stops waiting, so a stuck Valkey cannot pile up sends.
        var send = SendAsync(eventName, group, payload, organizationId);
        try
        {
            if (await send.WaitAsync(_options.PublishTimeout, _timeProvider, cancellationToken))
            {
                RealtimeTelemetry.Publishes.Add(1, tag);
            }
        }
        catch (TimeoutException)
        {
            RealtimeTelemetry.PublishFailures.Add(1, tag, new KeyValuePair<string, object?>("reason", "timeout"));
            LogTimedOut(_logger, eventName, organizationId);
        }
        catch (OperationCanceledException)
        {
            RealtimeTelemetry.PublishFailures.Add(1, tag, new KeyValuePair<string, object?>("reason", "cancelled"));
        }
    }

    // Returns whether the event was handed to Valkey; never throws, so a send that outlives its caller faults nobody.
    private async Task<bool> SendAsync(string eventName, string group, object payload, Guid organizationId)
    {
        try
        {
            await _hub.Clients.Group(group).SendAsync(eventName, payload, CancellationToken.None);
            return true;
        }
#pragma warning disable CA1031 // A live event is best effort: whatever the backplane throws must not reach the caller.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RealtimeTelemetry.PublishFailures.Add(
                1, new KeyValuePair<string, object?>("event", eventName), new KeyValuePair<string, object?>("reason", "error"));
            LogFailed(_logger, eventName, exception.GetType().Name, organizationId);
            return false;
        }
        finally
        {
            _slots.Release();
        }
    }

    // The exception type only: its message could carry the payload.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Publishing the live event {EventName} failed ({ExceptionType}) in organization {OrganizationId}.")]
    private static partial void LogFailed(ILogger logger, string eventName, string exceptionType, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Publishing the live event {EventName} timed out in organization {OrganizationId}.")]
    private static partial void LogTimedOut(ILogger logger, string eventName, Guid organizationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The live event {EventName} was dropped in organization {OrganizationId}: too many publishes in flight.")]
    private static partial void LogDropped(ILogger logger, string eventName, Guid organizationId);
}
