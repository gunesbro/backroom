using Microsoft.Extensions.Logging;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Live;

/// <summary>
/// Live events of the current request, held until its unit of work has committed (docs/contracts/phase-1.md, flow 4,
/// step 4). The endpoint's filter calls <see cref="FlushAsync"/> after the commit; a request that fails or rolls back
/// never flushes, so nothing is announced that was not stored. A failure to publish is logged with ids only and
/// never reaches the caller: the client has its answer, and catches up with a read.
/// </summary>
internal sealed partial class AfterCommitLiveEvents(ILiveEventPublisher publisher, ILogger<AfterCommitLiveEvents> logger)
{
    private readonly List<MessagePostedPayload> _messages = [];
    private readonly List<MessagePostedV2Payload> _messagesV2 = [];
    private readonly List<ChannelCreatedPayload> _channels = [];
    private readonly List<(Guid OrgId, Guid ConversationId, Guid AccountId)> _removals = [];

    public void Add(MessagePostedPayload payload) => _messages.Add(payload);

    public void Add(MessagePostedV2Payload payload) => _messagesV2.Add(payload);

    public void Add(ChannelCreatedPayload payload) => _channels.Add(payload);

    /// <summary>
    /// The account's membership of the conversation ended: after the commit, its connections leave the conversation's live
    /// group on every node (docs/contracts/phase-2.md, "Live events", removal). The outbox event repeats it durably.
    /// </summary>
    public void AddRemoval(Guid orgId, Guid conversationId, Guid accountId) => _removals.Add((orgId, conversationId, accountId));

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var messages = _messages.ToArray();
        var messagesV2 = _messagesV2.ToArray();
        var channels = _channels.ToArray();
        var removals = _removals.ToArray();
        _messages.Clear();
        _messagesV2.Clear();
        _channels.Clear();
        _removals.Clear();

        // Removals first: a removed account must not get what follows.
        foreach (var (orgId, conversationId, accountId) in removals)
        {
            try
            {
                await publisher.RemoveFromConversationAsync(orgId, conversationId, accountId, cancellationToken);
            }
#pragma warning disable CA1031 // Best effort: the consumer of the durable event repeats it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRemovalFailed(logger, exception.GetType().Name, orgId, conversationId);
            }
        }

        foreach (var channel in channels)
        {
            try
            {
                await publisher.PublishAsync(channel, cancellationToken);
            }
#pragma warning disable CA1031 // A live event is best effort: whatever the publisher throws must not fail the request.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogChannelCreatedFailed(logger, exception.GetType().Name, channel.OrgId, channel.Channel.Id);
            }
        }

        foreach (var message in messages)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
            }
#pragma warning disable CA1031 // See above.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogMessagePostedFailed(logger, exception.GetType().Name, message.OrgId, message.ChannelId, message.Message.Id);
            }
        }

        foreach (var message in messagesV2)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
            }
#pragma warning disable CA1031 // See above.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogMessagePostedFailed(logger, exception.GetType().Name, message.OrgId, message.ConversationId, message.Message.Id);
            }
        }
    }

    // The exception type only: its message could carry the payload.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Publishing the live event ChannelCreated failed ({ExceptionType}) in organization {OrganizationId}, channel {ChannelId}.")]
    private static partial void LogChannelCreatedFailed(ILogger logger, string exceptionType, Guid organizationId, Guid channelId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Publishing the live event MessagePosted failed ({ExceptionType}) in organization {OrganizationId}, channel {ChannelId}, message {MessageId}.")]
    private static partial void LogMessagePostedFailed(ILogger logger, string exceptionType, Guid organizationId, Guid channelId, Guid messageId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Removing a member from the live group failed ({ExceptionType}) in organization {OrganizationId}, conversation {ConversationId}.")]
    private static partial void LogRemovalFailed(ILogger logger, string exceptionType, Guid organizationId, Guid conversationId);
}
