using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Gateway;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// App role: listens on <see cref="SessionRevocationChannel"/>. For each revocation it takes the revocation into this node's
/// cache (so requests are refused at once, <see cref="RevocationChecker.Learn"/>) and closes this node's matching connections:
/// the session's, or, for a revocation of the whole account, those whose token was issued up to the revocation. Each of them first
/// gets <c>SessionRevoked</c> (the client signs out instead of reconnecting in a loop), then, after
/// <see cref="RealtimeOptions.RevocationCloseGrace"/>, the connection is aborted. Messages are handled in order, one at a time; they
/// come at the rate of sign-outs. Pub/sub delivers at most once: a node that missed a message still refuses the token within the
/// cache TTL (the request path reads Valkey), and a connection it left open ends when its token expires
/// (<c>CloseOnAuthenticationExpiration</c>, at most five minutes).
/// </summary>
internal sealed partial class SessionRevocationListener(
    ValkeyConnection valkey,
    ConnectionRegistry registry,
    RevocationChecker checker,
    IHubContext<RealtimeHub> hub,
    DelayedAbort aborts,
    IOptions<RealtimeOptions> options,
    TimeProvider timeProvider,
    ILogger<SessionRevocationListener> logger) : BackgroundService
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
                queue = await connection.GetSubscriber().SubscribeAsync(SessionRevocationChannel.Name);
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

    /// <summary>Applies one announced revocation on this node. Never throws.</summary>
    internal async Task HandleAsync(RedisValue message, CancellationToken cancellationToken)
    {
        if (!SessionRevocationChannel.TryDecode((byte[]?)message ?? [], out var revocation) || revocation is null)
        {
            LogMalformed(logger);
            return;
        }

        checker.Learn(revocation);
        var matching = registry.ConnectionsOf(revocation.AccountId).Where(state => Matches(state, revocation)).ToList();
        if (matching.Count == 0)
        {
            return;
        }

        // learned without delay however many connections this one closes.
        var notice = new SessionRevokedPayload(SessionRevokedPayload.SessionEnded);
        foreach (var state in matching)
        {
            try
            {
                await hub.Clients.Client(state.ConnectionId).SendAsync(LiveEventNames.SessionRevoked, notice, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A connection that cannot be told is closed anyway.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogNoticeFailed(logger, exception.GetType().Name, revocation.AccountId);
            }

            aborts.Schedule(state.Abort);
        }

        LogClosed(logger, revocation.AccountId, matching.Count);
    }

    private static bool Matches(ConnectionState state, SessionRevocation revocation) =>
        revocation.SessionId is { } sessionId
            ? string.Equals(state.SessionId, sessionId, StringComparison.Ordinal)
            : state.IssuedAt.ToUnixTimeSeconds() <= revocation.RevokedAt.ToUnixTimeSeconds();

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning, Message = "Subscribing to the session-revocation channel failed ({ExceptionType}); trying again.")]
    private static partial void LogSubscribeFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "A malformed session-revocation message was ignored.")]
    private static partial void LogMalformed(ILogger logger);

    [LoggerMessage(EventId = 34, Level = LogLevel.Warning, Message = "Telling a connection of account {AccountId} that its session was revoked failed ({ExceptionType}).")]
    private static partial void LogNoticeFailed(ILogger logger, string exceptionType, Guid accountId);

    [LoggerMessage(EventId = 35, Level = LogLevel.Information, Message = "Security: closed {Count} connections of account {AccountId} because the session was revoked.")]
    private static partial void LogClosed(ILogger logger, Guid accountId, int count);
}
