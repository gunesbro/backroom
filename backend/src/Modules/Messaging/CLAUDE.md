# Messaging (cell)

Contract: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md) (flow 4).

## Responsibility (Phase 1 core)

- Public channels: create, list.
- Plain-text messages in channels: send (idempotent by `clientMessageId`), list with keyset pagination (`before`, `after`, `limit`; `after` serves reconnect catch-up).
- Storage model: the server escapes the text and derives the structured blocks (one paragraph in Phase 1); client-supplied blocks are not accepted. Mentions table and the rich text editor arrive in Phase 2.
- Phase 2 adds DMs, group DMs, private channels, threads, replies, forwarding, reactions, read state, polls, scheduled messages, reminders, link previews.

## Owns

- Schema `messaging`: `channels`, `messages`, `outbox`.

## Publishes / depends on

- Integration event `messaging.message-posted.v1` (no text).
- Live events through `ILiveEventPublisher` (Realtime.Contracts) after commit: `MessagePosted` to the channel group, `ChannelCreated` to the organization group.
- `IPermissionChecker` (Permissions.Contracts); membership comes from the organization context.

## Rules

- Message content is never logged and never leaves the cell.
- Every table has `org_id` and RLS.
- Message ids are UUIDv7 and give the ordering.

## Status

- Implemented (Phase 1 step 6.1): projects Application, Contracts, Infrastructure, Presentation; schema `messaging` with `channels`, `messages` and `outbox`, all under RLS (migration `20261124120000_CreateChannelsAndMessages`). Queries are hand-written Npgsql SQL in the request's unit of work, like Organization.
- Channels: `POST`/`GET /api/v1/orgs/{orgId}/channels`. Create needs `channels.create-public`; both need membership (outsiders get `404 organization.not_found`). Names are trimmed, NFC, 1 to 80 characters, no control characters; unique among open channels per organization by `name_key` (`ToLowerInvariant`, never the current culture), else `409 messaging.channel_name_taken`. The list is keyset by id, oldest first, page size 1 to 100 (default 50).
- Messages: `POST .../channels/{channelId}/messages { clientMessageId, text }` and `GET ...?before=&after=&limit=`. The text is trimmed, 1 to 4,000 characters before escaping, no control characters except line breaks and tabs; it is stored HTML-escaped (`text`, and one paragraph in `blocks` derived on the server; client blocks are ignored). Sending is idempotent through the unique index `(channel_id, author_account_id, client_message_id)`: a repeat answers `200` with the stored message and publishes nothing. The message row and the `messaging.message-posted.v1` outbox row (ids only) are written in one transaction. Ids come from the monotonic UUIDv7 generator, so id order is send order within one process. Reads: `after` gives oldest first (catch-up), otherwise newest first (`before` pages back), `next` is the cursor for the following page or `null`, `before` with `after` is refused (`messaging.page_invalid`); an unknown, archived or other organization's channel is `404 messaging.channel_not_found`.
- Live events: `AfterCommitLiveEvents` holds `MessagePosted` and `ChannelCreated` until the unit of work has committed; the endpoint filter `PublishLiveEventsAfterCommit` (added before `InOrganization()` so it wraps the commit) flushes them only for a successful response. A publish failure is logged with ids and the exception type, never the payload, and never fails the request.
- Not yet: the Valkey and SignalR publisher (step 6.2; the host registers a no-op one meanwhile); archiving channels (the column and the filters exist); the feature flag (the endpoints are live behind authentication and membership).
