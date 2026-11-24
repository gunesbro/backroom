using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>
/// Leases due downstream messages for a stream and settles them when the cell answers
/// (docs/contracts/phase-1.md, "Delivery"): an <c>Ack</c> or non-retryable <c>Nack</c> calls the owning
/// module's <see cref="ICellMessageOutcomeHandler{T}"/> and settles the message in one unit of work; a
/// retryable <c>Nack</c> or a failing settlement delays the next attempt (2 s doubling to 5 min), and an
/// unanswered send counts as an attempt too. A message the cell answered while our handler failed keeps
/// that answer and is not sent again. After <see cref="MaxAttempts"/> the message ends (see
/// <see cref="ExpireAsync"/>), so its payload does not stay forever. Failures here never end the cell's
/// stream: the message stays pending and is tried again.
/// </summary>
internal sealed partial class DownstreamDelivery(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<DownstreamDelivery> logger)
{
    /// <summary>How long a sent message waits for its answer before it is sent again.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>About 3.5 hours of retries at the 5-minute ceiling.</summary>
    public const int MaxAttempts = 50;

    public const string DeliveryExpiredCode = "delivery_expired";

    public const string HandlerFailedCode = "handler_failed";

    /// <summary>Codes only the control plane sets; a cell's <c>Nack</c> cannot use them.</summary>
    public static bool IsReservedCode(string code) => code is DeliveryExpiredCode or HandlerFailedCode;

    private static readonly ActivitySource s_activitySource = new("Backroom.CellLink");

    /// <summary>Leases up to <paramref name="limit"/> due messages for the stream until their answer is due.</summary>
    public async Task<IReadOnlyList<OutboundMessage>> LeaseDueAsync(string cellId, Guid streamId, int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var now = timeProvider.GetUtcNow();
        return await scope.ServiceProvider.GetRequiredService<IOutboundMessages>()
            .LeaseDueAsync(cellId, streamId, now, now + AnswerTimeout, limit, cancellationToken);
    }

    /// <summary>Whether a leased message has used up its attempts and must be expired instead of sent.</summary>
    public static bool IsExhausted(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Attempts >= MaxAttempts;
    }

    /// <summary>
    /// Settles the cell's message: <paramref name="rejectedCode"/> null for an <c>Ack</c>. The cell's first
    /// answer wins: once stored (our handler failed), later answers settle it the same way.
    /// </summary>
    [SuppressMessage("Design", "CA1031", Justification = "A failing handler or a database error must not end the cell's stream; the message is retried.")]
    public async Task SettleAsync(string cellId, Guid messageId, string? rejectedCode, CancellationToken cancellationToken)
    {
        var answer = rejectedCode ?? OutboundMessage.AcknowledgedAnswer;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var messages = scope.ServiceProvider.GetRequiredService<IOutboundMessages>();
            await unitOfWork.BeginAsync(cancellationToken);
            if (await messages.LockPendingAsync(cellId, messageId, cancellationToken) is { } message)
            {
                var code = message.CellAnswer is null ? rejectedCode : message.RejectedCode;
                answer = code ?? OutboundMessage.AcknowledgedAnswer;
                await SettleLockedAsync(scope.ServiceProvider, messages, cellId, message, code, dispatch: true, cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
            return;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The scope's disposal rolled back the unit of work.
            LogSettleFailed(logger, exception.GetType().Name);
        }

        // Keep the cell's answer: the message is not sent again, and the settlement is retried after a backoff.
        await RetryLaterAsync(cellId, messageId, cancellationToken, answer);
    }

    /// <summary>
    /// A retryable <c>Nack</c> or a failed settlement: the next attempt waits for a backoff;
    /// <paramref name="cellAnswer"/> keeps the cell's answer if it gave one.
    /// </summary>
    public async Task RetryLaterAsync(string cellId, Guid messageId, CancellationToken cancellationToken, string? cellAnswer = null)
    {
        if (await ScheduleRetryAsync(cellId, messageId, cellAnswer, cancellationToken) is { Attempts: >= MaxAttempts })
        {
            await ExpireAsync(cellId, messageId, cancellationToken);
        }
    }

    /// <summary>
    /// Ends a message that used up its attempts, deleting its payload: one the cell never answered is rejected
    /// with <c>delivery_expired</c> (its handler is told); one the cell answered while our handler kept failing
    /// is closed as <c>handler_failed</c> without calling the handler again (an operations alert), and so is
    /// one whose expiry handler kept failing until twice the attempts.
    /// </summary>
    [SuppressMessage("Design", "CA1031", Justification = "A failing handler must not end the cell's stream; the expiry is tried again after a backoff.")]
    public async Task ExpireAsync(string cellId, Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var messages = scope.ServiceProvider.GetRequiredService<IOutboundMessages>();
            await unitOfWork.BeginAsync(cancellationToken);
            if (await messages.LockPendingAsync(cellId, messageId, cancellationToken) is { } message)
            {
                // A handler that keeps failing on the expiry too is given up at twice the attempts.
                if (message.CellAnswer is null && message.Attempts < 2 * MaxAttempts)
                {
                    await SettleLockedAsync(scope.ServiceProvider, messages, cellId, message, DeliveryExpiredCode, dispatch: true, cancellationToken);
                }
                else
                {
                    LogHandlerGaveUp(logger, message.Message.PayloadCase, cellId);
                    await SettleLockedAsync(scope.ServiceProvider, messages, cellId, message, HandlerFailedCode, dispatch: false, cancellationToken);
                }
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogSettleFailed(logger, exception.GetType().Name);
            await ScheduleRetryAsync(cellId, messageId, cellAnswer: null, cancellationToken);
        }
    }

    // Committed on its own, so the backoff holds whatever happens next.
    [SuppressMessage("Design", "CA1031", Justification = "A database error must not end the cell's stream; the lease ends and the message is sent again.")]
    private async Task<(int Attempts, string? CellAnswer)?> ScheduleRetryAsync(
        string cellId, Guid messageId, string? cellAnswer, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginAsync(cancellationToken);
            var retry = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>()
                .ScheduleRetryAsync(cellId, messageId, cellAnswer, timeProvider.GetUtcNow(), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return retry;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogRetryFailed(logger, exception.GetType().Name);
            return null;
        }
    }

    private async Task SettleLockedAsync(
        IServiceProvider services,
        IOutboundMessages messages,
        string cellId,
        OutboundMessage message,
        string? rejectedCode,
        bool dispatch,
        CancellationToken cancellationToken)
    {
        // A span per settlement, linked to the operation that enqueued the message.
        var envelope = message.Message.Envelope;
        IEnumerable<ActivityLink>? links = ActivityContext.TryParse(envelope.Traceparent, envelope.Tracestate, out var origin) ? [new ActivityLink(origin)] : null;
        using var activity = s_activitySource.StartActivity("cell-link settle", ActivityKind.Internal, default(ActivityContext), links: links);
        activity?.SetTag("backroom.cell_id", cellId);
        activity?.SetTag("backroom.link.message_type", message.Message.PayloadCase.ToString());
        activity?.SetTag("backroom.link.outcome", rejectedCode ?? "acknowledged");
        try
        {
            if (dispatch)
            {
                await DispatchAsync(services, cellId, message.MessageId, message.Message, rejectedCode, cancellationToken);
            }

            await messages.SettleAsync(cellId, message.MessageId, rejectedCode, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }

    private static Task DispatchAsync(
        IServiceProvider services, string cellId, Guid messageId, ConnectResponse message, string? rejectedCode, CancellationToken cancellationToken) =>
        message.PayloadCase switch
        {
            ConnectResponse.PayloadOneofCase.OrganizationProvisioned =>
                DispatchAsync(services, new CellMessage<OrganizationProvisioned>(messageId, cellId, message.OrganizationProvisioned), rejectedCode, cancellationToken),
            ConnectResponse.PayloadOneofCase.SessionRevoked =>
                DispatchAsync(services, new CellMessage<SessionRevoked>(messageId, cellId, message.SessionRevoked), rejectedCode, cancellationToken),
            ConnectResponse.PayloadOneofCase.InvitationAccepted =>
                DispatchAsync(services, new CellMessage<InvitationAccepted>(messageId, cellId, message.InvitationAccepted), rejectedCode, cancellationToken),
            ConnectResponse.PayloadOneofCase.MembershipSnapshotRequested =>
                DispatchAsync(services, new CellMessage<MembershipSnapshotRequested>(messageId, cellId, message.MembershipSnapshotRequested), rejectedCode, cancellationToken),
            ConnectResponse.PayloadOneofCase.AccountStatusChanged =>
                DispatchAsync(services, new CellMessage<AccountStatusChanged>(messageId, cellId, message.AccountStatusChanged), rejectedCode, cancellationToken),
            _ => throw new InvalidOperationException($"{message.PayloadCase} is not a durable downstream message."),
        };

    private static async Task DispatchAsync<T>(IServiceProvider services, CellMessage<T> message, string? rejectedCode, CancellationToken cancellationToken)
        where T : IMessage
    {
        foreach (var handler in services.GetServices<ICellMessageOutcomeHandler<T>>())
        {
            if (rejectedCode is null)
            {
                await handler.OnAcknowledgedAsync(message, cancellationToken);
            }
            else
            {
                await handler.OnRejectedAsync(message, rejectedCode, cancellationToken);
            }
        }
    }

    // Only the exception type: a handler's message could carry personal data from the payload.
    [LoggerMessage(Level = LogLevel.Error, Message = "Settling a link message failed with {ExceptionType}; it will be tried again.")]
    private static partial void LogSettleFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The handler for a {Type} link message to cell {CellId} kept failing after the cell answered; closed as handler_failed.")]
    private static partial void LogHandlerGaveUp(ILogger logger, ConnectResponse.PayloadOneofCase type, string cellId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduling a link message retry failed with {ExceptionType}; it will be sent again when its lease ends.")]
    private static partial void LogRetryFailed(ILogger logger, string exceptionType);
}
