# Messaging (cell)

Contracts: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md) (flow 4), [docs/contracts/phase-2.md](../../../../docs/contracts/phase-2.md) (Phase 2, in progress).

## Responsibility (Phase 1 core)

- Public channels: create, list.
- Plain-text messages in channels: send (idempotent by `clientMessageId`), list with keyset pagination (`before`, `after`, `limit`; `after` serves reconnect catch-up).
- Storage model: the server validates the text (plain text, NFC, 1 to 4,000 characters, never HTML-escaped) and derives the structured blocks (`[{ type: "paragraph", children: [{ type: "text", text }] }]`, plain strings; later rich text adds marks, mention nodes and list, quote and code block types); client-supplied blocks are not accepted. Escaping happens at render time in the clients. Each message gets a per-channel sequence number `seq` (commit order). Mentions table and the rich text editor arrive in Phase 2.
- Phase 2 adds DMs, group DMs, private channels, threads, replies, forwarding, reactions, read state, polls, scheduled messages, reminders, link previews.

## Owns

- Schema `messaging`: `conversations` (Phase 1 `channels`), `conversation_members`, `messages`, `outbox`.

## Publishes / depends on

- Integration event `messaging.message-posted.v1` (no text).
- Live events through `ILiveEventPublisher` (Realtime.Contracts) after commit: `MessagePosted` to the channel group, `ChannelCreated` to the organization group.
- `IPermissionChecker` (Permissions.Contracts); membership comes from the organization context.
- Integration events `messaging.conversation-created.v1`, `messaging.conversation-member-added.v1`, `messaging.conversation-member-removed.v1` (ids and the kind only).
- Publishes `IConversationReadAccess` (Contracts): whether an account may read a conversation (an open public channel, or an open conversation it is a member of), used by Realtime's subscription check.

## Rules

- Message content is never logged and never leaves the cell.
- Every table has `org_id` and RLS.
- Message ids are UUIDv7 and give the ordering.

## Status

- Implemented (Phase 1 step 6.1): projects Application, Contracts, Infrastructure, Presentation; schema `messaging` with `channels`, `messages` and `outbox`, all under RLS (migration `20261124120000_CreateChannelsAndMessages`). Queries are hand-written Npgsql SQL in the request's unit of work, like Organization.
- Channels: `POST`/`GET /api/v1/orgs/{orgId}/channels`. Create needs `channels.create-public`; both need membership (outsiders get `404 organization.not_found`). Names follow Directory's `DisplayText` rule (mirrored in `DisplayText.cs`: NFC, trimmed, at most 80 user-perceived characters, a visible character, no control or format characters except ZWNJ/ZWJ between characters); unique among open channels per organization by `name_key` (`ToLowerInvariant`, never the current culture), else `409 messaging.channel_name_taken`. The list is keyset by id, oldest first, page size 1 to 100 (default 50).
- Messages: `POST .../conversations/{conversationId}/messages { clientMessageId, text }` (needs an open membership: `403 messaging.not_a_member`) and `GET ...?before=&after=&limit=` answering `{ items, next }`; the Phase 1 `channels/{id}/messages` routes are removed; `GET .../conversations` lists the caller's open memberships (`{ items, next }`, keyset by id). `text` is stored raw and validated (see the storage model), `blocks` derived. In the send transaction `UPDATE channels SET last_seq = last_seq + 1 RETURNING last_seq` takes the channel row lock until commit, so `messages.seq` order is commit order (ids are assigned before the commit, so id order is not); sends to one channel are serialized to their commit, and `seq` has gaps. Sending is idempotent through the unique index `(channel_id, author_account_id, client_message_id)` (the repeat check relies on READ COMMITTED, checked by a test): a repeat answers `200` with the stored message and publishes nothing. The message row and the `messaging.message-posted.v1` outbox row (ids only) are written in one transaction; `messages (org_id, channel_id)` is a composite foreign key to `channels (org_id, id)`. Reads page by `seq`: `after` gives oldest first (catch-up, resume with `after={lastSeenSeq}`), otherwise newest first (`before` pages back); `next` is the cursor for the following page or `null`; `before` with `after` is refused (`messaging.page_invalid`); an unknown, archived or other organization channel is `404 messaging.conversation_not_found`.
- Live events: `AfterCommitLiveEvents` holds `MessagePosted` and `ChannelCreated` until the unit of work has committed; the endpoint filter `PublishLiveEventsAfterCommit` (added before `InOrganization()` so it wraps the commit) flushes them only for a successful response. A publish failure is logged with ids and the exception type, never the payload, and never fails the request.
- Live events are delivered by Realtime's `ValkeyLiveEventPublisher` (step 6.2); `IConversationReadAccess` (Contracts) answers Realtime's channel subscription check.
- Not yet: archiving channels (the column and the filters exist); the feature flag (the endpoints are live behind authentication and membership).

## Phase 2 step 1: conversations schema (human review)

- Migration `20261124150000_RenameChannelsToConversations`: `channels` → `conversations` in place (ids, names, `last_seq` and the RLS policy kept; `last_seq` and its assignment are unchanged, ADR 0012 owns them), with `kind` (`public_channel`, `private_channel`, `dm`, `group_dm`, `thread`; existing rows `public_channel`), `member_count`, and a name only for channel kinds; `messages.channel_id` → `conversation_id`. Name uniqueness (`conversations_public_name_key_idx`) covers open public channels only, so no answer reveals a private channel's name. New `conversation_members (org_id, conversation_id, account_id, role owner|member, joined_at, joined_seq, added_by, left_at)` under RLS, primary key `(conversation_id, account_id)` and partial index `(org_id, account_id, conversation_id) WHERE left_at IS NULL`; Phase 1 creators and authors were backfilled as members at the channel's `last_seq`. The data steps lift `FORCE ROW LEVEL SECURITY` inside the migration's transaction (the migrator owns the tables and would see no rows). `Down()` restores Phase 1 and refuses while any non-public conversation exists.
- Read access (`ConversationAccess.CanRead`, `IConversationReadAccess`): an open public channel for every active member, otherwise an open membership; threads and archived conversations are refused until their steps (thread: parent's access; archive: read-only but readable). Phase 1's channel routes use the same check, so a private conversation is `404` for non-members, Owner and Admin included; posting to a public channel still needs no join on those routes.
- Creating a channel makes the creator a member (`joined_seq` 0) and writes `conversation-created` and `conversation-member-added` to the outbox.
- Behind `Features:Conversations` (on in the AppHost): `POST /api/v1/orgs/{orgId}/conversations/{id}/join` (open public channels; a member gets `204` without a change; archived `409 messaging.conversation_archived`) and `/leave` (any member; DMs `409 messaging.cannot_leave_dm`; not a member `204`); unreadable, unknown and foreign conversations are the same `404 messaging.conversation_not_found`. At most 10,000 open memberships per person (`409 messaging.membership_limit`; concurrent joins can exceed it by their number). Membership and `member_count` change in one statement; lock order membership row, then conversation row.
- Left for later steps: live `MembershipChanged` and the removal broadcast (step 2), the conversation routes for messages and the removal of the Phase 1 routes (step 3), ownership transfer when a private channel's owner leaves (step 7), `change_seq` / `last_message_seq` (step 5).
