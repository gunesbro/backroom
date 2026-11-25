using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Modules.Notifications.Application.Settings;
using Backroom.Modules.Presence.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Notifications.Application.Delivery;

/// <summary>
/// <c>messaging.activity-added.v1</c> → one bell entry per person who may get one, and a live event to those whose settings
/// deliver it (docs/contracts/phase-2.md, flow 8). Never for the actor, never for a conversation the person cannot read
/// (the same check as reading it), at most one entry per message and person. Runs in the event's organization.
/// </summary>
/// <remarks>
/// The live events are queued as after-commit actions (<see cref="AfterCommitActions"/>, run by the consumer host once the unit of
/// work has committed): a failed commit announces nothing, and a redelivery skipped by the inbox announces nothing. The preview text
/// is read inside the unit of work, once per message (every recipient already passed the same read check). Publishing is best effort
/// and never fails the delivery. Ids only in logs, never text.
/// </remarks>
internal sealed partial class ActivityAddedHandler(
    INotificationStore store,
    IConversationReadAccess conversations,
    IOrganizationMembershipCheck members,
    IMessagePreviews previews,
    IPresenceReader presence,
    ILiveEventPublisher live,
    AfterCommitActions afterCommit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILogger<ActivityAddedHandler> logger) : IIntegrationEventHandler<ActivityAdded>
{
    public async Task HandleAsync(ActivityAdded integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (!ActivityKinds.IsKnown(integrationEvent.Kind))
        {
            LogUnknownKind(logger, integrationEvent.EventId, integrationEvent.OrgId);
            return;
        }

        var organizationId = integrationEvent.OrgId;
        var now = StoredTime.Now(timeProvider);
        var recipients = (integrationEvent.AccountIds ?? [])
            .Where(account => account != integrationEvent.ActorAccountId)
            .Distinct()
            .Take(ActivityAdded.MaxAccounts)
            .ToList();

        // Only active organization members who may read the conversation, in one batch (a removed member or a guest gets
        // nothing). Kinds that point at no message or conversation need no read check; a message without a conversation is malformed.
        if (integrationEvent.ConversationId is { } conversationId)
        {
            recipients = [.. await conversations.FilterReadersAsync(organizationId, conversationId, recipients, cancellationToken)];
        }
        else if (integrationEvent.MessageId is not null)
        {
            LogMalformed(logger, integrationEvent.EventId, organizationId);
            return;
        }
        else
        {
            recipients = [.. await members.SelectActiveMembersAsync(recipients, cancellationToken)];
        }

        if (recipients.Count == 0)
        {
            return;
        }

        var accountSettings = await store.GetAccountSettingsAsync(recipients, cancellationToken);
        var conversationSettings = integrationEvent.ConversationId is { } id
            ? await store.GetConversationSettingsAsync(id, recipients, cancellationToken)
            : new Dictionary<Guid, ConversationSettings>();
        var doNotDisturb = await DoNotDisturbAsync(recipients, now, cancellationToken);

        var entries = new List<NewInboxItem>(recipients.Count);
        var outcomes = new Dictionary<Guid, Outcome>(recipients.Count);
        foreach (var account in recipients)
        {
            accountSettings.TryGetValue(account, out var settings);
            conversationSettings.TryGetValue(account, out var conversation);
            var outcome = DeliveryRules.Decide(
                integrationEvent.Kind, doNotDisturb.Contains(account), conversation, settings ?? AccountSettings.Default, now);
            if (outcome == Outcome.Drop)
            {
                continue;
            }

            outcomes[account] = outcome;
            entries.Add(new NewInboxItem(
                ids.NewId(), organizationId, account, integrationEvent.Kind, integrationEvent.ConversationId, integrationEvent.MessageId,
                integrationEvent.ActorAccountId, now));
        }

        if (entries.Count == 0)
        {
            return;
        }

        var added = await store.AddItemsAsync(entries, cancellationToken);
        var toAnnounce = entries.Where(entry => added.Contains(entry.Id) && outcomes[entry.AccountId] == Outcome.Deliver).ToList();
        if (toAnnounce.Count > 0)
        {
            // Read once as the first recipient: every recipient passed the same read check for this conversation.
            string? text = null;
            if (integrationEvent.MessageId is { } messageId && toAnnounce.Any(entry => !SettingsOf(accountSettings, entry.AccountId).HidePreviews))
            {
                var texts = await previews.ReadAsync(
                    organizationId, toAnnounce[0].AccountId, [messageId], NotificationRequestedPayload.MaxPreviewLength, cancellationToken);
                text = texts.GetValueOrDefault(messageId);
            }

            foreach (var entry in toAnnounce)
            {
                var payload = new NotificationRequestedPayload(
                    entry.OrgId, entry.AccountId, entry.Id, entry.Kind, entry.ConversationId, entry.MessageId, entry.ActorAccountId,
                    SettingsOf(accountSettings, entry.AccountId).HidePreviews ? null : text);
                afterCommit.Add(token => PublishAsync(payload, token));
            }
        }

        LogDecided(logger, integrationEvent.EventId, organizationId, recipients.Count, added.Count);
    }

    private static AccountSettings SettingsOf(IReadOnlyDictionary<Guid, AccountSettings> settings, Guid accountId) =>
        settings.TryGetValue(accountId, out var found) ? found : AccountSettings.Default;

    private async Task<HashSet<Guid>> DoNotDisturbAsync(List<Guid> accounts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = new HashSet<Guid>();
        for (var offset = 0; offset < accounts.Count; offset += IPresenceReader.MaxAccounts)
        {
            var chunk = accounts.Skip(offset).Take(IPresenceReader.MaxAccounts).ToArray();
            foreach (var entry in await presence.ReadAsync(chunk, cancellationToken))
            {
                if (entry.DndUntil is { } until && until > now)
                {
                    active.Add(entry.AccountId);
                }
            }
        }

        return active;
    }

    private async Task PublishAsync(NotificationRequestedPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            await live.PublishAsync(payload, cancellationToken);
        }
#pragma warning disable CA1031 // Best effort: a live event can be lost, the bell has the entry.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogAnnounceFailed(logger, exception.GetType().Name, payload.OrgId, payload.NotificationId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Event {EventId} of organization {OrganizationId} has an unknown activity kind and was ignored.")]
    private static partial void LogUnknownKind(ILogger logger, Guid eventId, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Event {EventId} of organization {OrganizationId}: {Recipients} recipients, {Added} bell entries added.")]
    private static partial void LogDecided(ILogger logger, Guid eventId, Guid organizationId, int recipients, int added);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Event {EventId} of organization {OrganizationId} names a message but no conversation and was ignored.")]
    private static partial void LogMalformed(ILogger logger, Guid eventId, Guid organizationId);

    // The exception type only: its message could carry the payload.
    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Announcing notification {NotificationId} of organization {OrganizationId} failed ({ExceptionType}).")]
    private static partial void LogAnnounceFailed(ILogger logger, string exceptionType, Guid organizationId, Guid notificationId);
}
