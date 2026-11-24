using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// Worker role: keeps the cell's stream to the control plane (docs/contracts/phase-1.md, "Cell ↔ control
/// plane link"). Once the cell is registered and this worker holds the lease, it connects with the
/// cell's token, sends <c>Hello</c> and a <c>Heartbeat</c> every 30 s, reconnects with a fresh token
/// when the current one leaves the cache (shortly before it expires), and retries failures with
/// exponential backoff and full jitter (1 s to 60 s). Over the stream it sends the outbox
/// (<see cref="UpstreamDelivery"/>) and passes downstream messages to <see cref="DownstreamReceiver"/>.
/// </summary>
internal sealed partial class CellLinkWorker(
    CellKeyStore keyStore,
    CellCredentials credentials,
    UpstreamDelivery upstream,
    UpstreamOutcomes outcomes,
    DownstreamReceiver downstream,
    FullSyncRunner fullSync,
    CellLinkStatus status,
    [FromKeyedServices(LinkLease.DataSourceKey)] NpgsqlDataSource leaseDataSource,
    IHttpClientFactory httpClientFactory,
    IOptions<ControlPlaneConnectorOptions> options,
    TimeProvider timeProvider,
    ILogger<CellLinkWorker> logger) : BackgroundService
{
    public const string HttpClientName = "control-plane-link";

    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The FullSync interval when Welcome names none the cell accepts (the contract: every 15 minutes).</summary>
    internal static readonly TimeSpan DefaultFullSyncInterval = TimeSpan.FromMinutes(15);

    /// <summary>A token with less time left than this is not used for a stream.</summary>
    internal static readonly TimeSpan MinimumStreamLifetime = TimeSpan.FromSeconds(10);

    /// <summary>How long a sent upstream message waits for its answer before it counts as an attempt.</summary>
    internal static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often the outbox is polled for due messages while connected (one indexed query).</summary>
    internal static readonly TimeSpan DeliveryPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Upstream messages sent and not yet answered, per stream.</summary>
    internal const int MaxInFlight = 100;

    internal const string LinkRejectedReason =
        "The control plane refused the cell's link (permission denied): the cell is registered locally but not on the control plane, or its token is not a cell token.";

    internal const string CredentialsRejectedReason =
        "Keycloak refused the cell's credentials: the cell is registered locally but Keycloak does not hold its client or key.";

    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan s_registrationPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(60);

    private static readonly string s_version =
        typeof(CellLinkWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private volatile bool _connected;
    private int _failures;
    private int _connections;
    private int _attempts;

    /// <summary>Whether the stream is open and welcomed; for diagnostics and tests.</summary>
    internal bool IsConnected => _connected;

    /// <summary>How many streams were welcomed; for diagnostics and tests.</summary>
    internal int Connections => Volatile.Read(ref _connections);

    /// <summary>How many streams were attempted; for diagnostics and tests.</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    [SuppressMessage("Design", "CA1031", Justification = "The link survives control plane, Keycloak and network outages; failures are logged and retried.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitUntilRegisteredAsync(stoppingToken);
                await using var lease = await AcquireLeaseAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Returns normally when the token is due for renewal: reconnect at once.
                    await RunStreamAsync(lease, stoppingToken);
                }
            }
            catch (Exception exception) when (stoppingToken.IsCancellationRequested
                && exception is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
            {
                return;
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.PermissionDenied)
            {
                // The control plane does not accept this cell (for example "not registered" although the local marker
                // says so). Retrying cannot fix it, and re-registering is not allowed: report it and keep backing off.
                status.Reject(LinkRejectedReason);
                LogLinkRejected(logger, options.Value.CellId, exception.StatusCode);
            }
            catch (RpcException exception)
            {
                LogStreamEnded(logger, exception.StatusCode);
            }
            catch (CellCredentialsRejectedException exception)
            {
                status.Reject(CredentialsRejectedReason);
                LogCredentialsRejected(logger, options.Value.CellId, exception.Status);
            }
            catch (Exception exception)
            {
                LogLinkFailed(logger, exception);
            }

            _failures++;
            try
            {
                await Task.Delay(Backoff(_failures), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Full jitter on an exponential base, so cells do not reconnect in step after an outage.
    internal static TimeSpan Backoff(int failures)
    {
        var ceiling = Math.Min(s_maxBackoff.TotalMilliseconds, 1000 * Math.Pow(2, Math.Min(failures, 10)));
        return TimeSpan.FromMilliseconds(1000 + (RandomNumberGenerator.GetInt32(1000) / 1000.0 * Math.Max(0, ceiling - 1000)));
    }

    private async Task WaitUntilRegisteredAsync(CancellationToken cancellationToken)
    {
        while (!keyStore.IsRegistered)
        {
            await Task.Delay(s_registrationPoll, timeProvider, cancellationToken);
        }
    }

    private async Task<LinkLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var waiting = false;
        while (true)
        {
            if (await LinkLease.TryAcquireAsync(leaseDataSource, options.Value.CellId, cancellationToken) is { } lease)
            {
                return lease;
            }

            if (!waiting)
            {
                LogWaitingForLease(logger);
                waiting = true;
            }

            await Task.Delay(options.Value.LinkLeaseRetryInterval, timeProvider, cancellationToken);
        }
    }

    private async Task RunStreamAsync(LinkLease lease, CancellationToken stoppingToken)
    {
        // A worker whose lease connection died must not take the stream from the rightful holder.
        await lease.CheckAsync(stoppingToken);

        var settings = options.Value;
        Interlocked.Increment(ref _attempts);
        var (token, refreshAt) = await credentials.GetAccessTokenWithRefreshTimeAsync(forceRefresh: false, stoppingToken);
        var lifetime = refreshAt - timeProvider.GetUtcNow();
        if (lifetime < MinimumStreamLifetime)
        {
            // The cached token is nearly due (timer granularity, clock adjustment): fetch a fresh one.
            (token, refreshAt) = await credentials.GetAccessTokenWithRefreshTimeAsync(forceRefresh: true, stoppingToken);
            lifetime = refreshAt - timeProvider.GetUtcNow();
            if (lifetime < MinimumStreamLifetime)
            {
                throw new InvalidOperationException("The cell's access token is too close to expiry to open a stream.");
            }
        }

        using var renewal = new CancellationTokenSource(lifetime, timeProvider);
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, renewal.Token);
        using var channel = GrpcChannel.ForAddress(settings.ControlPlaneUrl!, new GrpcChannelOptions
        {
            HttpClient = httpClientFactory.CreateClient(HttpClientName),
            DisposeHttpClient = true,
            MaxReceiveMessageSize = MaxMessageBytes,
            MaxSendMessageSize = MaxMessageBytes,
        });
        var client = new CellLinkService.CellLinkServiceClient(channel);
        using var call = client.Connect(new Metadata { { "Authorization", "Bearer " + token } }, cancellationToken: stream.Token);
        using var writer = new LinkWriter(call.RequestStream, timeProvider);
        DateTimeOffset? welcomedAt = null;
        var inFlight = new ConcurrentDictionary<Guid, InFlightMessage>();
        var window = new SendWindow();

        try
        {
            await writer.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = settings.CellId, Version = s_version } }, stream.Token);
            var fullSyncInterval = await ReadWelcomeAsync(call.ResponseStream, stream.Token);
            _connected = true;
            status.Clear();
            welcomedAt = timeProvider.GetUtcNow();
            Interlocked.Increment(ref _connections);
            LogConnected(logger, settings.CellId);

            var heartbeats = SendHeartbeatsAsync(writer, lease, stream.Token);
            var sends = SendDueAsync(writer, inFlight, window, stream.Token);
            var reads = ReadAsync(call.ResponseStream, writer, inFlight, window, stream.Token);
            // Drift repair: revoked sessions the cell may have missed, now and at the interval the control plane asked for.
            var fullSyncs = RunFullSyncsAsync(client, token, fullSyncInterval, stream.Token);
            Task first;
            try
            {
                first = await Task.WhenAny(heartbeats, sends, reads, fullSyncs);
            }
            finally
            {
                // Whatever ended first, stop and await the other loops before the writer, call and lease are released.
                await stream.CancelAsync();
                await Task.WhenAll(heartbeats, sends, reads, fullSyncs).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            // The control plane ended the stream (seen by the reader, whichever loop failed first): only the
            // oldest unanswered message is charged an attempt, so a message that breaks the stream every time
            // expires without taking the messages behind it along. The cell's own failures charge nothing.
            if (reads.Exception?.InnerException is RpcException { StatusCode: not StatusCode.Cancelled }
                && !stoppingToken.IsCancellationRequested && !inFlight.IsEmpty)
            {
                await ChargeOldestAsync(inFlight, stoppingToken);
            }

            await first;
        }
        catch (Exception exception) when (renewal.IsCancellationRequested && !stoppingToken.IsCancellationRequested
            && exception is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
        {
            // Renewal: the stream is closed on purpose and opened again with a fresh token.
        }
        finally
        {
            _connected = false;

            // Only a stream that stayed up resets the backoff, so a link that keeps dropping right after
            // Welcome (overload, rolling restart) still backs off.
            if (welcomedAt is { } at && timeProvider.GetUtcNow() - at >= HeartbeatInterval)
            {
                _failures = 0;
            }
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The original stream error is rethrown by the caller; a failed charge only means the message is not charged this time.")]
    private async Task ChargeOldestAsync(ConcurrentDictionary<Guid, InFlightMessage> inFlight, CancellationToken cancellationToken)
    {
        try
        {
            await upstream.RetryLaterAsync(inFlight.MinBy(entry => entry.Value.Sequence).Key, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogChargeFailed(logger, exception.GetType().Name);
        }
    }

    private async Task<TimeSpan> ReadWelcomeAsync(IAsyncStreamReader<ConnectResponse> responses, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(WelcomeTimeout, timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (!await responses.MoveNext(timeout.Token) || responses.Current.PayloadCase != ConnectResponse.PayloadOneofCase.Welcome)
        {
            throw new InvalidOperationException("The control plane did not answer Hello with Welcome.");
        }

        // The control plane says how often to repair drift; a value outside one minute to one day falls back to 15 minutes.
        var seconds = responses.Current.Welcome.FullSyncIntervalSeconds;
        return seconds is >= 60 and <= 86_400 ? TimeSpan.FromSeconds(seconds) : DefaultFullSyncInterval;
    }

    // The first FullSync runs at once, the next ones at the interval. A failed run is logged and tried again at the next
    // interval; it never ends the stream, because SessionRevoked messages are the main path and FullSync only repairs.
    [SuppressMessage("Design", "CA1031", Justification = "FullSync is a repair; its failure must not end the link.")]
    private async Task RunFullSyncsAsync(CellLinkService.CellLinkServiceClient client, string token, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, timeProvider);
        do
        {
            try
            {
                await fullSync.RunAsync(client, token, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogFullSyncFailed(logger, exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task SendHeartbeatsAsync(LinkWriter writer, LinkLease lease, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            // Losing the lease's connection means another worker may take over: stop sending at once.
            await lease.CheckAsync(cancellationToken);
            await writer.WriteAsync(
                new ConnectRequest { Heartbeat = new Heartbeat { Version = s_version, Health = CellHealth.Healthy } },
                cancellationToken);
        }
    }

    // Sends the outbox's due messages in sequence order, at most MaxInFlight unanswered; one unanswered for
    // AnswerTimeout counts an attempt and is sent again after its backoff. A database error ends the stream
    // (the worker reconnects with backoff, and every pending message is sent again).
    // A new stream sends one message first (the least-tried one) and opens the full window at the first
    // answer, so a message that broke earlier streams neither goes first again nor takes others along.
    private async Task SendDueAsync(
        LinkWriter writer, ConcurrentDictionary<Guid, InFlightMessage> inFlight, SendWindow window, CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var (messageId, sent) in inFlight)
            {
                if (now - sent.SentAt >= AnswerTimeout && inFlight.TryRemove(messageId, out _))
                {
                    await upstream.RetryLaterAsync(messageId, cancellationToken);
                }
            }

            var free = window.IsOpen ? MaxInFlight - inFlight.Count : 1 - inFlight.Count;
            if (free > 0)
            {
                foreach (var message in await upstream.TakeDueAsync([.. inFlight.Keys], free, probe: !window.IsOpen, cancellationToken))
                {
                    inFlight[Guid.Parse(message.Envelope.MessageId)] = new InFlightMessage(now, message.Envelope.Sequence, UpstreamOutcomes.For(message));
                    await writer.WriteAsync(message, cancellationToken);
                }
            }

            await Task.Delay(DeliveryPollInterval, timeProvider, cancellationToken);
        }
    }

    private async Task ReadAsync(
        IAsyncStreamReader<ConnectResponse> responses,
        LinkWriter writer,
        ConcurrentDictionary<Guid, InFlightMessage> inFlight,
        SendWindow window,
        CancellationToken cancellationToken)
    {
        while (await responses.MoveNext(cancellationToken))
        {
            var response = responses.Current;
            switch (response.PayloadCase)
            {
                case ConnectResponse.PayloadOneofCase.Ack:
                    // Answers to messages not sent on this stream are ignored.
                    if (Guid.TryParse(response.Ack.MessageId, out var acknowledged) && inFlight.TryRemove(acknowledged, out var sent))
                    {
                        window.Open();

                        // The outcome before the settlement: if it fails, the message is sent again and the
                        // outcome retried, instead of being lost with the settled payload.
                        if (sent.Outcome is { } outcome && !await outcomes.ApplyAsync(outcome, cancellationToken))
                        {
                            await upstream.RetryLaterAsync(acknowledged, cancellationToken);
                        }
                        else
                        {
                            await upstream.SettleAsync(acknowledged, rejectedCode: null, cancellationToken);
                        }
                    }

                    break;
                case ConnectResponse.PayloadOneofCase.Nack:
                    LogNack(logger, response.Nack.Code, response.Nack.Retryable);
                    if (Guid.TryParse(response.Nack.MessageId, out var refused) && inFlight.TryRemove(refused, out _))
                    {
                        window.Open();
                        if (response.Nack.Retryable)
                        {
                            await upstream.RetryLaterAsync(refused, cancellationToken);
                        }
                        else
                        {
                            var code = NackCodePattern().IsMatch(response.Nack.Code) ? response.Nack.Code : "invalid_code";
                            await upstream.SettleAsync(refused, code, cancellationToken);
                        }
                    }

                    break;
                case ConnectResponse.PayloadOneofCase.Welcome:
                    throw new InvalidOperationException("Welcome is sent once.");
                case ConnectResponse.PayloadOneofCase.None:
                    await writer.NackAsync(response.Envelope, "unsupported_type", retryable: false, cancellationToken);
                    break;
                default:
                    var (answer, retryable) = await downstream.ReceiveAsync(response, cancellationToken);
                    if (answer is null)
                    {
                        await writer.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = response.Envelope.MessageId } }, cancellationToken);
                    }
                    else
                    {
                        await writer.NackAsync(response.Envelope, answer, retryable, cancellationToken);
                    }

                    break;
            }
        }

        throw new RpcException(new Status(StatusCode.Unavailable, "The control plane closed the stream."));
    }

    [GeneratedRegex(@"\A[a-z0-9_.]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NackCodePattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Charging the oldest unanswered link message after a broken stream failed with {ExceptionType}.")]
    private static partial void LogChargeFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cell {CellId} connected its link to the control plane.")]
    private static partial void LogConnected(ILogger logger, string cellId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FullSync failed with {ExceptionType}; trying again at the next interval.")]
    private static partial void LogFullSyncFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Another worker holds the link; waiting.")]
    private static partial void LogWaitingForLease(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The link to the control plane ended with {Status}; reconnecting.")]
    private static partial void LogStreamEnded(ILogger logger, StatusCode status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Security: the control plane refused the link of cell {CellId} with {Status}; the local registration does not match the control plane. Retrying with backoff; the cell does not re-register on its own.")]
    private static partial void LogLinkRejected(ILogger logger, string cellId, StatusCode status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Security: Keycloak refused the credentials of cell {CellId} with status {Status}; the local registration does not match Keycloak. Retrying with backoff; the cell does not re-register on its own.")]
    private static partial void LogCredentialsRejected(ILogger logger, string cellId, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The link to the control plane failed; reconnecting.")]
    private static partial void LogLinkFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The control plane refused a message: {Code} (retryable: {Retryable}).")]
    private static partial void LogNack(ILogger logger, string code, bool retryable);

    /// <summary>Serializes writes to the request stream (heartbeats and answers come from two loops).</summary>
    private sealed class LinkWriter(IClientStreamWriter<ConnectRequest> stream, TimeProvider timeProvider) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private ulong _sequence;

        public async Task WriteAsync(ConnectRequest request, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                // Durable messages keep their outbox sequence; control messages get a per-stream counter.
                request.Envelope ??= new Envelope();
                if (request.Envelope.Sequence == 0)
                {
                    request.Envelope.Sequence = ++_sequence;
                }

                request.Envelope.SentAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());
                await stream.WriteAsync(request, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task NackAsync(Envelope? envelope, string code, bool retryable, CancellationToken cancellationToken) =>
            WriteAsync(
                new ConnectRequest { Nack = new Nack { MessageId = envelope?.MessageId ?? string.Empty, Code = code, Retryable = retryable } },
                cancellationToken);

        public void Dispose() => _lock.Dispose();
    }

    private readonly record struct InFlightMessage(DateTimeOffset SentAt, ulong Sequence, UpstreamOutcome? Outcome);

    /// <summary>Whether the stream may have more than one upstream message unanswered (after its first answer).</summary>
    private sealed class SendWindow
    {
        private volatile bool _open;

        public bool IsOpen => _open;

        public void Open() => _open = true;
    }
}
