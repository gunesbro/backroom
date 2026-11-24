# Messaging (cell)

Contract: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md) (flow 4).

## Responsibility (Phase 1 core)

- Public channels: create, list.
- Plain-text messages in channels: send (idempotent by `clientMessageId`), list with keyset pagination (`before`, `after`, `limit`; `after` serves reconnect catch-up).
- Storage model: the server validates the text (plain text, NFC, 1 to 4,000 characters, never HTML-escaped) and derives the structured blocks (`[{ type: "paragraph", children: [{ type: "text", text }] }]`, plain strings; later rich text adds marks, mention nodes and list, quote and code block types); client-supplied blocks are not accepted. Escaping happens at render time in the clients. Each message gets a per-channel sequence number `seq` (commit order). Mentions table and the rich text editor arrive in Phase 2.
- Phase 2 adds DMs, group DMs, private channels, threads, replies, forwarding, reactions, read state, polls, scheduled messages, reminders, link previews.

## Owns

- Schema `messaging`: `channels`, `messages`, `outbox`.

## Publishes / depends on

- Integration event `messaging.message-posted.v1` (no text).
- Live events through `ILiveEventPublisher` (Realtime.Contracts) after commit: `MessagePosted` to the channel group, `ChannelCreated` to the organization group.
- `IPermissionChecker` (Permissions.Contracts); membership comes from the organization context.
- Publishes `IChannelReadAccess` (Contracts): whether an account may read a channel (Phase 1: an open public channel), used by Realtime's subscription check.
- Publishes `IMessageFileAccess` (Contracts): whether an account may read a message that carries a file, asked by Files on every download of an attached file. Until attachments (Phase 2 step 22) no message carries files, so it answers false.

## Rules

- Message content is never logged and never leaves the cell.
- Every table has `org_id` and RLS.
- Message ids are UUIDv7 and give the ordering.

## Status

- Implemented (Phase 1 step 6.1): projects Application, Contracts, Infrastructure, Presentation; schema `messaging` with `channels`, `messages` and `outbox`, all under RLS (migration `20261124120000_CreateChannelsAndMessages`). Queries are hand-written Npgsql SQL in the request's unit of work, like Organization.
- Channels: `POST`/`GET /api/v1/orgs/{orgId}/channels`. Create needs `channels.create-public`; both need membership (outsiders get `404 organization.not_found`). Names follow Directory's `DisplayText` rule (mirrored in `DisplayText.cs`: NFC, trimmed, at most 80 user-perceived characters, a visible character, no control or format characters except ZWNJ/ZWJ between characters); unique among open channels per organization by `name_key` (`ToLowerInvariant`, never the current culture), else `409 messaging.channel_name_taken`. The list is keyset by id, oldest first, page size 1 to 100 (default 50).
- Messages: `POST .../channels/{channelId}/messages { clientMessageId, text }` and `GET ...?before=&after=&limit=`. `text` is stored raw and validated (see the storage model), `blocks` derived. In the send transaction `UPDATE channels SET last_seq = last_seq + 1 RETURNING last_seq` takes the channel row lock until commit, so `messages.seq` order is commit order (ids are assigned before the commit, so id order is not); sends to one channel are serialized to their commit, and `seq` has gaps. Sending is idempotent through the unique index `(channel_id, author_account_id, client_message_id)` (the repeat check relies on READ COMMITTED, checked by a test): a repeat answers `200` with the stored message and publishes nothing. The message row and the `messaging.message-posted.v1` outbox row (ids only) are written in one transaction; `messages (org_id, channel_id)` is a composite foreign key to `channels (org_id, id)`. Reads page by `seq`: `after` gives oldest first (catch-up, resume with `after={lastSeenSeq}`), otherwise newest first (`before` pages back); `next` is the cursor for the following page or `null`; `before` with `after` is refused (`messaging.page_invalid`); an unknown, archived or other organization channel is `404 messaging.channel_not_found`.
- Live events: `AfterCommitLiveEvents` holds `MessagePosted` and `ChannelCreated` until the unit of work has committed; the endpoint filter `PublishLiveEventsAfterCommit` (added before `InOrganization()` so it wraps the commit) flushes them only for a successful response. A publish failure is logged with ids and the exception type, never the payload, and never fails the request.
- Live events are delivered by Realtime's `ValkeyLiveEventPublisher` (step 6.2); `IChannelReadAccess` (Contracts) answers Realtime's channel subscription check.
- Not yet: archiving channels (the column and the filters exist); the feature flag (the endpoints are live behind authentication and membership).
