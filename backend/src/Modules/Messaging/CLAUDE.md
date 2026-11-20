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
