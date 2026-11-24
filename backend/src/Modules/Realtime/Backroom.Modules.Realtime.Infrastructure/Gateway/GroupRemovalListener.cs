using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// App role: listens on <see cref="GroupRemovalChannel"/>'s two channels and removes this node's connections of the
/// account from every group of the organization (member removed from the organization) or from one conversation's group
/// (member removed from the conversation). Messages are handled one at a time in arrival order per channel; they are
/// 32 or 48 bytes and come at the rate of members being removed, so the listener keeps no queue worth bounding.
/// Removing is idempotent. Pub/sub delivers at most once, so a node that was cut off from Valkey can miss one; the
/// connection then keeps the group only until its token expires (<c>CloseOnAuthenticationExpiration</c>, at most five
/// minutes), and the reconnect has to pass the checks again.
/// </summary>
internal sealed partial class GroupRemovalListener(
    ValkeyConnection valkey,
    ConnectionRegistry registry,
    IHubContext<RealtimeHub> hub,
    IOptions<RealtimeOptions> options,
    TimeProvider timeProvider,
    ILogger<GroupRemovalListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var organizations = await SubscribeAsync(GroupRemovalChannel.Name, stoppingToken);
        if (organizations is null)
        {
            return;
        }

        organizations.OnMessage(message => HandleAsync(message.Message, stoppingToken));
        var conversations = await SubscribeAsync(GroupRemovalChannel.ConversationName, stoppingToken);
        if (conversations is not null)
        {
            conversations.OnMessage(message => HandleConversationAsync(message.Message, stoppingToken));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }

            await conversations.UnsubscribeAsync();
        }

        await organizations.UnsubscribeAsync();
    }

    // Valkey may be down when the node starts; the connection retries in the background, and so does the subscription.
    private async Task<ChannelMessageQueue?> SubscribeAsync(RedisChannel channel, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await valkey.GetAsync(stoppingToken);
                return await connection.GetSubscriber().SubscribeAsync(channel);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return null;
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
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>Removes this node's connections of the account from every group of the organization. Never throws.</summary>
    internal async Task HandleAsync(RedisValue message, CancellationToken cancellationToken)
    {
        if (!GroupRemovalChannel.TryDecode((byte[]?)message ?? [], out var organizationId, out var accountId))
        {
            LogMalformed(logger);
            return;
        }

        await RemoveAsync(accountId, organizationId, state => state.GroupsOf(organizationId), cancellationToken);
        LogRemoved(logger, accountId, organizationId);
    }

    /// <summary>Removes this node's connections of the account from the conversation's group. Never throws.</summary>
    internal async Task HandleConversationAsync(RedisValue message, CancellationToken cancellationToken)
    {
        if (!GroupRemovalChannel.TryDecodeConversation((byte[]?)message ?? [], out var organizationId, out var conversationId, out var accountId))
        {
            LogMalformed(logger);
            return;
        }

        var group = GroupNames.Conversation(options.Value.CellId, organizationId, conversationId);
        await RemoveAsync(accountId, organizationId, state => state.Contains(group) ? [group] : [], cancellationToken);
        LogRemovedFromConversation(logger, accountId, conversationId, organizationId);
    }

    // Under each connection's gate: a subscribe holds it from its check to its join, so one that passed its check before
    // the membership ended has joined by now and is removed here.
    private async Task RemoveAsync(Guid accountId, Guid organizationId, Func<ConnectionState, List<string>> groupsOf, CancellationToken cancellationToken)
    {
        foreach (var state in registry.ConnectionsOf(accountId))
        {
            try
            {
                await state.Gate.WaitAsync(cancellationToken);
                try
                {
                    foreach (var group in groupsOf(state))
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
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Subscribing to a group-removal channel failed ({ExceptionType}); trying again.")]
    private static partial void LogSubscribeFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "A malformed group-removal message was ignored.")]
    private static partial void LogMalformed(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Removing a connection from the groups failed ({ExceptionType}) in organization {OrganizationId}, account {AccountId}.")]
    private static partial void LogRemovalFailed(ILogger logger, string exceptionType, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Removed the connections of account {AccountId} from the groups of organization {OrganizationId}.")]
    private static partial void LogRemoved(ILogger logger, Guid accountId, Guid organizationId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Removed the connections of account {AccountId} from conversation {ConversationId} of organization {OrganizationId}.")]
    private static partial void LogRemovedFromConversation(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId);
}
