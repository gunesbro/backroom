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
    private readonly List<ChannelCreatedPayload> _channels = [];

    public void Add(MessagePostedPayload payload) => _messages.Add(payload);

    public void Add(ChannelCreatedPayload payload) => _channels.Add(payload);

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var messages = _messages.ToArray();
        var channels = _channels.ToArray();
        _messages.Clear();
        _channels.Clear();

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
    }

    // The exception type only: its message could carry the payload.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Publishing the live event ChannelCreated failed ({ExceptionType}) in organization {OrganizationId}, channel {ChannelId}.")]
    private static partial void LogChannelCreatedFailed(ILogger logger, string exceptionType, Guid organizationId, Guid channelId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Publishing the live event MessagePosted failed ({ExceptionType}) in organization {OrganizationId}, channel {ChannelId}, message {MessageId}.")]
    private static partial void LogMessagePostedFailed(ILogger logger, string exceptionType, Guid organizationId, Guid channelId, Guid messageId);
}
