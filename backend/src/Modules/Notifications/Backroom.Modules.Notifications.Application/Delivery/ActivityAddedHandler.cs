using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
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
/// The live events leave at the end of the handler, before the unit of work commits (a handler has no after-commit hook).
/// If the commit then fails, the redelivery stores the entries again and announces them again, so the worst case is a
/// repeated toast; an entry is never announced without having been decided. Publishing is best effort and never fails
/// the handler. Ids only in logs, never text.
/// </remarks>
internal sealed partial class ActivityAddedHandler(
    INotificationStore store,
    IConversationReadAccess conversations,
    IMessagePreviews previews,
    IPresenceReader presence,
    ILiveEventPublisher live,
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

        // The same permission check as reading the conversation (Messaging answers false for anything unreadable).
        if (integrationEvent.ConversationId is { } conversationId)
        {
            var allowed = new List<Guid>(recipients.Count);
            foreach (var account in recipients)
            {
                if (await conversations.CanReadAsync(organizationId, conversationId, account, cancellationToken))
                {
                    allowed.Add(account);
                }
            }

            recipients = allowed;
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
        foreach (var entry in entries)
        {
            if (!added.Contains(entry.Id) || outcomes[entry.AccountId] != Outcome.Deliver)
            {
                continue;
            }

            await AnnounceAsync(entry, accountSettings.GetValueOrDefault(entry.AccountId) ?? AccountSettings.Default, cancellationToken);
        }

        LogDecided(logger, integrationEvent.EventId, organizationId, recipients.Count, added.Count);
    }

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

    private async Task AnnounceAsync(NewInboxItem entry, AccountSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            string? preview = null;
            if (!settings.HidePreviews && entry.MessageId is { } messageId)
            {
                // Read as the recipient, through the permission check; at most 150 characters.
                var texts = await previews.ReadAsync(
                    entry.OrgId, entry.AccountId, [messageId], NotificationRequestedPayload.MaxPreviewLength, cancellationToken);
                preview = texts.GetValueOrDefault(messageId);
            }

            await live.PublishAsync(
                new NotificationRequestedPayload(
                    entry.OrgId, entry.AccountId, entry.Id, entry.Kind, entry.ConversationId, entry.MessageId, entry.ActorAccountId, preview),
                cancellationToken);
        }
#pragma warning disable CA1031 // Best effort: a live event can be lost, the bell has the entry.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogAnnounceFailed(logger, exception.GetType().Name, entry.OrgId, entry.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Event {EventId} of organization {OrganizationId} has an unknown activity kind and was ignored.")]
    private static partial void LogUnknownKind(ILogger logger, Guid eventId, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Event {EventId} of organization {OrganizationId}: {Recipients} recipients, {Added} bell entries added.")]
    private static partial void LogDecided(ILogger logger, Guid eventId, Guid organizationId, int recipients, int added);

    // The exception type only: its message could carry the payload.
    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Announcing notification {NotificationId} of organization {OrganizationId} failed ({ExceptionType}).")]
    private static partial void LogAnnounceFailed(ILogger logger, string exceptionType, Guid organizationId, Guid notificationId);
}
