using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>
/// The control plane's end of the link (docs/contracts/phase-1.md, "Cell ↔ control plane link"): one
/// stream per registered cell, identified by its token, never by the payload. This stage handles
/// <c>Hello</c>/<c>Welcome</c> and heartbeats; durable messages get a retryable <c>Nack</c> until their
/// handlers exist, so the cell keeps them.
/// </summary>
internal sealed partial class CellLinkEndpoint(
    ICellRegistry registry,
    CellLinkSessions sessions,
    DownstreamDelivery delivery,
    UpstreamReceiver upstream,
    IDirectoryQueries directory,
    IRevokedSessionQueries revocations,
    TimeProvider timeProvider,
    ILogger<CellLinkEndpoint> logger) : CellLinkService.CellLinkServiceBase
{
    /// <summary>How long a new stream may wait for its <c>Hello</c>.</summary>
    internal static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Heartbeats arrive every 30 s; more frequent ones are not written again unless the version changes.</summary>
    internal static readonly TimeSpan HeartbeatWriteInterval = TimeSpan.FromSeconds(20);

    /// <summary>Even a version change is written at most this often, so a cell cannot flood the registry.</summary>
    internal static readonly TimeSpan MinimumWriteGap = TimeSpan.FromSeconds(1);

    /// <summary>The <c>FullSync</c> and <c>MembershipDigest</c> interval sent in <c>Welcome</c>.</summary>
    internal const uint FullSyncIntervalSeconds = 15 * 60;

    /// <summary>Downstream messages sent and not yet answered, per stream.</summary>
    internal const int MaxInFlight = 100;

    /// <summary>
    /// Answers to messages not sent on the stream, and refused upstream messages, each tolerated per minute
    /// before the stream is closed.
    /// </summary>
    internal const int MaxUnknownAnswersPerMinute = 300;

    /// <summary>How often a connected cell's due messages are looked up (one indexed query).</summary>
    internal static readonly TimeSpan DeliveryPollInterval = TimeSpan.FromSeconds(1);

    public override async Task Connect(
        IAsyncStreamReader<ConnectRequest> requestStream, IServerStreamWriter<ConnectResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var user = context.GetHttpContext().User;
        if (!CellClients.TryGetCellId(user, out var cellId)
            || !long.TryParse(user.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAtSeconds))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Not a cell token."));
        }

        if (!await registry.IsRegisteredAsync(cellId, context.CancellationToken))
        {
            LogRejected(logger, cellId, "cell not registered");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The cell is not registered."));
        }

        // Hello is checked before the stream counts as the cell's, so a bad new stream never replaces a good one.
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds);
        var hello = await ReadHelloAsync(requestStream, expiresAt, context.CancellationToken);
        if (!string.Equals(hello.CellId, cellId, StringComparison.Ordinal))
        {
            LogRejected(logger, cellId, "Hello names another cell");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Hello.cell_id does not match the token."));
        }

        var version = CheckVersion(hello.Version);
        using var session = sessions.Open(cellId, expiresAt, context.CancellationToken);
        try
        {
            // The cell learns its epoch here; the same statement records the heartbeat. A cell that stopped being
            // registered since the check above is refused like an unregistered one.
            var epoch = await registry.RecordHeartbeatAsync(cellId, version, session.Ended)
                ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "The cell is not registered."));
            using var writer = new ResponseWriter(responseStream, timeProvider);
            await writer.WriteAsync(
                new ConnectResponse { Welcome = new Welcome { FullSyncIntervalSeconds = FullSyncIntervalSeconds, Epoch = (ulong)epoch } },
                session.Ended);
            LogConnected(logger, cellId);

            // Sending due messages and reading the cell's answers run side by side; when one ends, both stop.
            using var loops = CancellationTokenSource.CreateLinkedTokenSource(session.Ended);
            // Leases are per stream: messages an older stream of this cell leased are sent again on this one.
            var streamId = Guid.CreateVersion7();
            var inFlight = new ConcurrentDictionary<Guid, DateTimeOffset>();
            var sending = SendDueAsync(cellId, streamId, writer, inFlight, loops.Token);
            var reading = ReadAsync(requestStream, writer, cellId, version, epoch, inFlight, loops.Token);
            Task first;
            try
            {
                first = await Task.WhenAny(sending, reading);
            }
            finally
            {
                await loops.CancelAsync();
                await Task.WhenAll(sending, reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await first;
        }
        catch (OperationCanceledException) when (session.IsEpochChanged)
        {
            LogEpochChanged(logger, cellId);
            throw EpochChanged();
        }
        catch (OperationCanceledException) when (session.IsSuperseded)
        {
            LogSuperseded(logger, cellId);
            throw new RpcException(new Status(StatusCode.Aborted, "A newer stream of this cell replaced this one."));
        }
        catch (OperationCanceledException) when (session.IsExpired)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "The token expired; reconnect with a fresh one."));
        }
    }

    // Sends the cell's due messages in sequence order, at most MaxInFlight unanswered at a time. Each sent
    // message is leased until its answer is due; an unanswered one is sent again when its lease ends, and
    // after a reconnect every pending one is sent again.
    private async Task SendDueAsync(
        string cellId, Guid streamId, ResponseWriter writer, ConcurrentDictionary<Guid, DateTimeOffset> inFlight, CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var (messageId, leaseUntil) in inFlight)
            {
                if (leaseUntil <= now)
                {
                    inFlight.TryRemove(messageId, out _);
                }
            }

            var free = MaxInFlight - inFlight.Count;
            if (free > 0)
            {
                foreach (var message in await delivery.LeaseDueAsync(cellId, streamId, free, cancellationToken))
                {
                    if (DownstreamDelivery.IsExhausted(message))
                    {
                        await delivery.ExpireAsync(cellId, message.MessageId, cancellationToken);
                        continue;
                    }

                    // The cell already answered and our handler failed: retry the settlement, do not send again.
                    if (message.CellAnswer is not null)
                    {
                        await delivery.SettleAsync(cellId, message.MessageId, message.RejectedCode, cancellationToken);
                        continue;
                    }

                    inFlight[message.MessageId] = now + DownstreamDelivery.AnswerTimeout;
                    await writer.WriteAsync(message.Message, cancellationToken);
                }
            }

            await Task.Delay(DeliveryPollInterval, timeProvider, cancellationToken);
        }
    }

    private async Task ReadAsync(
        IAsyncStreamReader<ConnectRequest> requestStream,
        ResponseWriter writer,
        string cellId,
        string version,
        long epoch,
        ConcurrentDictionary<Guid, DateTimeOffset> inFlight,
        CancellationToken cancellationToken)
    {
        var lastWrite = timeProvider.GetUtcNow();
        var unknownAnswers = new AnswerWindow(timeProvider);
        var refusedMessages = new AnswerWindow(timeProvider);
        while (await requestStream.MoveNext(cancellationToken))
        {
            var request = requestStream.Current;
            switch (request.PayloadCase)
            {
                case ConnectRequest.PayloadOneofCase.Heartbeat:
                    var reported = CheckVersion(request.Heartbeat.Version);
                    var now = timeProvider.GetUtcNow();
                    var since = now - lastWrite;
                    if (since >= HeartbeatWriteInterval
                        || (since >= MinimumWriteGap && !string.Equals(reported, version, StringComparison.Ordinal)))
                    {
                        // An epoch incremented on another control-plane instance is noticed here (at most every 20 s
                        // to 30 s), so the cell fences its writes and reconnects to learn it.
                        if (await registry.RecordHeartbeatAsync(cellId, reported, cancellationToken) != epoch)
                        {
                            LogEpochChanged(logger, cellId);
                            throw EpochChanged();
                        }

                        (version, lastWrite) = (reported, now);
                    }

                    break;
                case ConnectRequest.PayloadOneofCase.Ack:
                    // Only answers to messages sent on this stream are settled; settling also filters by the
                    // authenticated cell, so a cell can settle only its own messages.
                    if (Guid.TryParse(request.Ack.MessageId, out var acknowledged) && inFlight.TryRemove(acknowledged, out _))
                    {
                        await delivery.SettleAsync(cellId, acknowledged, rejectedCode: null, cancellationToken);
                    }
                    else
                    {
                        unknownAnswers.Count();
                    }

                    break;
                case ConnectRequest.PayloadOneofCase.Nack:
                    if (Guid.TryParse(request.Nack.MessageId, out var refused) && inFlight.TryRemove(refused, out _))
                    {
                        if (request.Nack.Retryable)
                        {
                            await delivery.RetryLaterAsync(cellId, refused, cancellationToken);
                        }
                        else
                        {
                            var code = NackCodePattern().IsMatch(request.Nack.Code) && !DownstreamDelivery.IsReservedCode(request.Nack.Code)
                                ? request.Nack.Code
                                : "invalid_code";
                            await delivery.SettleAsync(cellId, refused, code, cancellationToken);
                        }
                    }
                    else
                    {
                        unknownAnswers.Count();
                    }

                    break;
                case ConnectRequest.PayloadOneofCase.Hello:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Hello is sent once."));
                case ConnectRequest.PayloadOneofCase.None:
                    // A payload type this version does not know (contract, "Versions").
                    await writer.NackAsync(request.Envelope, "unsupported_type", retryable: false, cancellationToken);
                    break;
                default:
                    if (string.IsNullOrEmpty(request.Envelope?.MessageId))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "A durable message needs a message_id."));
                    }

                    var outcome = await upstream.ReceiveAsync(cellId, request, cancellationToken);
                    if (outcome.Code is null)
                    {
                        await writer.WriteAsync(new ConnectResponse { Ack = new Ack { MessageId = request.Envelope.MessageId } }, cancellationToken);
                    }
                    else
                    {
                        // Refusals cost a transaction and a log line each: a flood of them closes the stream.
                        if (!outcome.Retryable)
                        {
                            refusedMessages.Count();
                        }

                        await writer.NackAsync(request.Envelope, outcome.Code, outcome.Retryable, cancellationToken);
                    }

                    break;
            }
        }
    }

    /// <summary>The most organizations one <c>FullSync</c> answer lists (about 1.5 MB; the link's message limit is 4 MB).</summary>
    internal const int MaxSyncedOrganizations = 10_000;

    // The state the cell must hold (contract, "Drift repair"): its organizations and the revocations in force of accounts
    // that are members in it. The cell comes from the token, never from the request.
    public override async Task<FullSyncResponse> FullSync(FullSyncRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.GetHttpContext().User;
        if (!CellClients.TryGetCellId(user, out var cellId))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Not a cell token."));
        }

        if (!await registry.IsRegisteredAsync(cellId, context.CancellationToken))
        {
            LogRejected(logger, cellId, "cell not registered");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The cell is not registered."));
        }

        var response = new FullSyncResponse();
        foreach (var organization in await directory.ListOrganizationsOfCellAsync(cellId, MaxSyncedOrganizations, context.CancellationToken))
        {
            response.Organizations.Add(new OrganizationAssignment { OrganizationId = organization.OrganizationId.ToString("D"), Name = organization.Name });
        }

        foreach (var revoked in await revocations.ListForCellAsync(cellId, context.CancellationToken))
        {
            response.RevokedSessions.Add(new SessionRevoked
            {
                AccountId = revoked.AccountId.ToString("D"),
                SessionId = revoked.SessionId ?? string.Empty,
                RevokedAt = Timestamp.FromDateTimeOffset(revoked.RevokedAt),
                KeepUntil = Timestamp.FromDateTimeOffset(revoked.KeepUntil),
            });
        }

        return response;
    }

    // Waits for Hello until the timeout or the token's expiry, whichever comes first.
    private async Task<Hello> ReadHelloAsync(IAsyncStreamReader<ConnectRequest> requestStream, DateTimeOffset expiresAt, CancellationToken callAborted)
    {
        var untilExpiry = expiresAt - timeProvider.GetUtcNow();
        var wait = untilExpiry < HelloTimeout ? untilExpiry : HelloTimeout;
        using var deadline = new CancellationTokenSource(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callAborted, deadline.Token);
        try
        {
            if (await requestStream.MoveNext(timeout.Token) && requestStream.Current.PayloadCase == ConnectRequest.PayloadOneofCase.Hello)
            {
                return requestStream.Current.Hello;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callAborted.IsCancellationRequested)
        {
            throw wait < HelloTimeout
                ? new RpcException(new Status(StatusCode.Unauthenticated, "The token expired; reconnect with a fresh one."))
                : new RpcException(new Status(StatusCode.DeadlineExceeded, "No Hello received."));
        }

        throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be Hello."));
    }

    /// <summary>The status that ends a stream whose cell epoch changed (cell_link.proto, <c>Welcome.epoch</c>).</summary>
    internal const string EpochChangedDetail = "cell.epoch_changed";

    private static RpcException EpochChanged() => new(new Status(StatusCode.FailedPrecondition, EpochChangedDetail));

    private static string CheckVersion(string version) =>
        VersionPattern().IsMatch(version)
            ? version
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "The version must be 1 to 64 letters, digits, '.', '+' or '-'."));

    [GeneratedRegex(@"\A[0-9A-Za-z.+-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[a-z0-9_.]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NackCodePattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "Security: cell {CellId} connected its link.")]
    private static partial void LogConnected(ILogger logger, string cellId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security: link of cell {CellId} rejected: {Reason}.")]
    private static partial void LogRejected(ILogger logger, string cellId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security: the epoch of cell {CellId} changed; its link was ended so it learns the new epoch.")]
    private static partial void LogEpochChanged(ILogger logger, string cellId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The link of cell {CellId} was replaced by a newer stream.")]
    private static partial void LogSuperseded(ILogger logger, string cellId);

    /// <summary>Serializes writes to the response stream (the sender and the reader both write).</summary>
    private sealed class ResponseWriter(IServerStreamWriter<ConnectResponse> stream, TimeProvider timeProvider) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async Task WriteAsync(ConnectResponse response, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                response.Envelope ??= new Envelope();
                response.Envelope.SentAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());
                await stream.WriteAsync(response, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task NackAsync(Envelope? envelope, string code, bool retryable, CancellationToken cancellationToken) =>
            WriteAsync(
                new ConnectResponse { Nack = new Nack { MessageId = envelope?.MessageId ?? string.Empty, Code = code, Retryable = retryable } },
                cancellationToken);

        public void Dispose() => _lock.Dispose();
    }

    /// <summary>
    /// Late answers (after a lease ended) are normal and harmless; a flood of answers to nothing is not.
    /// A fixed one-minute window, so a long-lived stream never hits the limit by accident.
    /// </summary>
    private sealed class AnswerWindow(TimeProvider timeProvider)
    {
        private DateTimeOffset _windowStart = timeProvider.GetUtcNow();
        private int _count;

        public void Count()
        {
            var now = timeProvider.GetUtcNow();
            if (now - _windowStart >= TimeSpan.FromMinutes(1))
            {
                (_windowStart, _count) = (now, 0);
            }

            if (++_count > MaxUnknownAnswersPerMinute)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Too many unknown answers or refused messages on this stream."));
            }
        }
    }
}
