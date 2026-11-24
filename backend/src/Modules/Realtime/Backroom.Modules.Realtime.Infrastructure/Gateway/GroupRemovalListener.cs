using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// App role: listens on <see cref="GroupRemovalChannel"/> and, for each message, removes this node's connections of the
/// account from the organization's groups. Messages are handled one at a time in arrival order; they are 32 bytes and
/// come at the rate of members being removed, so the listener keeps no queue worth bounding. Pub/sub delivers at most
/// once, so a node that was cut off from Valkey can miss one; the connection then lives only until its token expires
/// (<c>CloseOnAuthenticationExpiration</c>, at most five minutes), and the reconnect has to pass the membership check.
/// </summary>
internal sealed partial class GroupRemovalListener(
    ValkeyConnection valkey,
    ConnectionRegistry registry,
    IHubContext<RealtimeHub> hub,
    Backroom.Modules.Presence.Contracts.IPresenceTracker presence,
    IOptions<RealtimeOptions> options,
    TimeProvider timeProvider,
    ILogger<GroupRemovalListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Valkey may be down when the node starts; the connection retries in the background, and so does the subscription.
        ChannelMessageQueue? queue = null;
        while (queue is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await valkey.GetAsync(stoppingToken);
                queue = await connection.GetSubscriber().SubscribeAsync(GroupRemovalChannel.Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Any failure to subscribe is retried; the node serves its clients meanwhile.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSubscribeFailed(logger, exception.GetType().Name);
                try
                {
                    await Task.Delay(options.Value.ListenerRetryDelay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (queue is null)
        {
            return;
        }

        queue.OnMessage(message => HandleAsync(message.Message, stoppingToken));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }

        await queue.UnsubscribeAsync();
    }

    /// <summary>Removes this node's connections of the account from the organization's groups. Never throws.</summary>
    internal async Task HandleAsync(RedisValue message, CancellationToken cancellationToken)
    {
        if (!GroupRemovalChannel.TryDecode((byte[]?)message ?? [], out var organizationId, out var accountId))
        {
            LogMalformed(logger);
            return;
        }

        foreach (var state in registry.ConnectionsOf(accountId))
        {
            try
            {
                await state.Gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var group in state.GroupsOf(organizationId))
                    {
                        state.Remove(group);
                        RealtimeTelemetry.Subscriptions.Add(-1);
                        await hub.Groups.RemoveFromGroupAsync(state.ConnectionId, group, cancellationToken);
                    }
                }
                finally
                {
                    state.Gate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One connection failing must not keep the others in the groups.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRemovalFailed(logger, exception.GetType().Name, organizationId, accountId);
            }
        }

        // Watchers in that organization stop watching the person: their presence group goes on every node (this message reaches
        // all), and their nodes stop renewing the watch. A member removal is rare, so a pass over this node's connections is fine.
        presence.Unwatch(organizationId, accountId);
        var watched = GroupNames.Presence(options.Value.CellId, organizationId, accountId);
        foreach (var state in registry.All())
        {
            try
            {
                await state.Gate.WaitAsync(cancellationToken);
                try
                {
                    if (state.Watched.Contains(watched))
                    {
                        state.Remove(watched);
                        RealtimeTelemetry.Subscriptions.Add(-1);
                        await hub.Groups.RemoveFromGroupAsync(state.ConnectionId, watched, cancellationToken);
                    }
                }
                finally
                {
                    state.Gate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One connection failing must not keep the others in the group.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRemovalFailed(logger, exception.GetType().Name, organizationId, accountId);
            }
        }

        LogRemoved(logger, accountId, organizationId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Subscribing to the member-removal channel failed ({ExceptionType}); trying again.")]
    private static partial void LogSubscribeFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "A malformed member-removal message was ignored.")]
    private static partial void LogMalformed(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Removing a connection from the groups failed ({ExceptionType}) in organization {OrganizationId}, account {AccountId}.")]
    private static partial void LogRemovalFailed(ILogger logger, string exceptionType, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Removed the connections of account {AccountId} from the groups of organization {OrganizationId}.")]
    private static partial void LogRemoved(ILogger logger, Guid accountId, Guid organizationId);
}
