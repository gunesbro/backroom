# Notifications (cell)

Contract: [docs/contracts/phase-2.md](../../../../docs/contracts/phase-2.md) (flow 8 "Notifications", "HTTP APIs", "Data", "Integration events", "Live events", decision 13, step 29). **Human review required** (permission checks, previews, privacy rules).

## Responsibility

- Preferences, the decision order, the bell and the live `NotificationRequested` event. Layers: Application (decision rules, handler, bell and settings services, store interface), Infrastructure (store, migration, registration), Presentation (endpoints). No Contracts project yet: nothing consumes Notifications (the `notifications.settings-changed.v1` event for Messaging's `mention_settings` arrives with its step).
- `messaging.activity-added.v1` (`ActivityAdded`, in Messaging.Contracts) → `ActivityAddedHandler` (worker role): drops the actor, drops everyone who may not read the conversation (`IConversationReadAccess`, the same check as reading it), reads do not disturb from `IPresenceReader`, applies `DeliveryRules.Decide`, stores the bell entries in one statement and sends the live event only for entries that were newly added.

## Owns

- Schema `notifications` (organization-scoped, forced RLS on every table): `account_settings`, `conversation_settings`, `inbox_items` (ids only; unique per account and message, keyset index on `(created_at, id)`, partial indexes for the Mentions tab and unread rows), `inbox` (the consumer's inbox). Web Push subscriptions and the 30-day retention job are not built yet.

## Depends on

- Messaging.Contracts: `IConversationReadAccess`, `IMessagePreviews` (text read through the read check; none for a person who lost access, a deleted or unknown message), `ActivityAdded`, `ActivityKinds`.
- Presence.Contracts: `IPresenceReader` (do not disturb). Realtime.Contracts: `ILiveEventPublisher.PublishAsync(NotificationRequestedPayload)`, which goes to the recipient's account group only.

## Rules

- Decision order (`DeliveryRules`): the conversation setting (all, mentions, none; ignore `@kanal`/`@burada`; followed thread replies), else organization mute (silent, bell only), else the account default; do not disturb then turns a delivery into a silent bell entry. Decision beyond the contract: do not disturb never adds an entry the settings drop (a conversation set to "nothing" stays out of the bell). Leave and off-hours join in Phase 4.
- Bell rows hold ids only. Text shown with an entry (list) is read through `IMessagePreviews` at most 150 characters, as the signed-in person. The live event carries at most 150 characters, or none when the person hides previews. Never log text, names or previews; logs carry ids and the exception type.
- At most one entry per message per person (unique index); a redelivered event adds and announces nothing.
- The live event is sent at the end of the handler, before its unit of work commits (a handler has no after-commit hook); the worst case after a failed commit is a repeated toast. Publishing is best effort.
- Every list is bounded: page size 1 to 100 (default 30), cursor from a previous page, at most 500 recipients per event.

## HTTP (behind `Features:Notifications`, app role)

- `GET /notifications?tab=all|mentions&after=&limit=`, `POST /notifications/read-all`, `PUT /notifications/{id}/read` (own entries; `404 notifications.not_found`).
- `GET`/`PUT /notification-settings`, `GET`/`PUT /conversations/{id}/notification-settings` (read access to the conversation; `404 notifications.conversation_not_found`; `400 notifications.level_invalid`, `notifications.mute_invalid`).

## Status

- Implemented (step 29, server slice): everything above, behind `Features:Notifications` (on in development).
- Tests: `Backroom.Modules.Notifications.IntegrationTests` (real PostgreSQL with RLS, fake neighbours): decision order, do not disturb landing silently in the bell, recipient-only delivery, no preview after access is lost, organization and account isolation (`Category=Privacy`). Messaging's `MessagePreviewTests` cover the real preview check.
- Not yet: the producer of `messaging.activity-added.v1` (Messaging's feed writer), mute from `IConversationPrefs` (Messaging), `notifications.settings-changed.v1`, the 30-day retention job, Web Push, web and desktop notifications and the tray, the unread count, a batch read check (`CanReadAsync` runs once per recipient, at most 500 per event; a batch method in Messaging.Contracts replaces it), HTTP-level tests with real tokens.
