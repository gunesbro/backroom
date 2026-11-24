using Microsoft.Extensions.Logging;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// Evaluates a person and publishes <c>PresenceChanged</c> to their presence group in each organization where someone watches them
/// (never to a whole organization). The evaluation decides whether and when (at most one event per person per
/// <see cref="PresenceOptions.EventInterval"/>, coalesced); the publisher is best effort and bounded on its own. Logs carry ids only.
/// </summary>
internal sealed partial class PresencePublisher(PresenceStore store, ILiveEventPublisher live, TimeProvider timeProvider, ILogger<PresencePublisher> logger)
{
    /// <summary>Evaluates and publishes; never throws for Valkey or publish failures (they are logged and counted).</summary>
    public async Task EvaluateAsync(Guid accountId, CancellationToken cancellationToken)
    {
        PresenceEvaluation? evaluation;
        try
        {
            evaluation = await store.EvaluateAsync(accountId, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            PresenceTelemetry.Failures.Add(1, new KeyValuePair<string, object?>("operation", "evaluate"));
            LogEvaluationFailed(logger, accountId, exception.GetType().Name);
            return;
        }

        if (evaluation is null)
        {
            return;
        }

        var state = PresenceRules.StateName(evaluation.State);
        var dnd = PresenceRules.Iso(evaluation.DndUntil);
        var custom = evaluation.CustomStatus is { } status
            ? new LiveCustomStatus(status.Emoji, status.Text, PresenceRules.Iso(status.ExpiresAt))
            : null;
        foreach (var organizationId in evaluation.Organizations)
        {
            // The publisher never throws and waits a bounded time.
            await live.PublishAsync(new PresenceChangedPayload(organizationId, accountId, state, dnd, custom), cancellationToken);
        }

        PresenceTelemetry.Changes.Add(1, new KeyValuePair<string, object?>("state", state));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Evaluating the presence of account {AccountId} failed ({ExceptionType}); it is tried again later.")]
    private static partial void LogEvaluationFailed(ILogger logger, Guid accountId, string exceptionType);
}
