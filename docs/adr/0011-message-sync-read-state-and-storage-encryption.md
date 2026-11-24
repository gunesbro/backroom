# 0011. Message sync, read state and storage encryption

- Status: Proposed
- Date: 2026-11-24

## Context

People must never miss a message. They refresh the page, sign in on another device after days away, lose the connection, or get a reply in a thread whose root is 150 messages back in a channel they already read. Live events (SignalR) can be lost during a reconnect, and a node can restart mid-delivery. So live delivery alone cannot tell a client what changed.

Phase 1 has public channels only. Each channel has a commit-ordered `seq` (decision 12 in the Phase 1 contract), and clients catch up with `GET .../messages?after={lastSeenSeq}`. Phase 2 adds DMs, group DMs, private channels, threads, mentions, read state and unread counts across devices (requirements, "Mesajlaşma" and "Kanallar, DM'ler ve kenar çubuğu"). Their design must fit what Phase 1 builds now.

The requirements also fix the encryption model ("Şifreleme ve anahtarlar"). TLS protects data in transit. Encryption at rest is server-side, covering the database, object storage and backups. There is no end-to-end encryption, deliberately. Enterprise customers can bring their own key (BYOK). Search is PostgreSQL full-text search behind `ISearchIndex`, and it goes through the same permission check as regular reads.

## Decision

### Live events are hints; the server state is the truth

A client never derives correctness from live events. Every start, reload and reconnect asks the server what changed since the client's cursors. Live events only make that faster.

### Commit-ordered sequences per conversation

- Every conversation has a `last_seq` and every message a `seq`, assigned in the send transaction under the conversation row's lock (`UPDATE … SET last_seq = last_seq + 1 RETURNING last_seq`). The lock lasts until commit, so `seq` order is commit order, and `after={seq}` can never skip a message. Gaps are allowed: a lost race on an idempotent repeat leaves one.
- Conversations are channels (public and private), DMs, group DMs and threads. A thread is its own conversation: a reply increments the thread's `seq`, not the channel's. The channel records only the thread's latest activity on its root message.
- The lock serializes sends within one conversation, which allows roughly one send per commit time. That is ample for chat. A conversation that needs more is a broadcast feature with its own design.

### Read state on the server

- `messaging.read_states (org_id, account_id, conversation_id, last_read_seq, updated_at)` holds one row per person and conversation.
- Unread counts are computed on read: the conversation's `last_seq` minus `last_read_seq`, with hidden and deleted messages accounted for. Sending a message writes no per-member rows, so the write cost stays constant however large the channel.
- "Mark read", "mark unread" and the "new" line all come from `last_read_seq`. A change is stored on the server, so it applies on every device, and it is pushed to the person's other connections as a live event.

### Thread followers and a personal activity feed

- `messaging.thread_followers (org_id, thread_id, account_id)` lists followers. The thread's author, everyone who replies and everyone mentioned in it follow it automatically, and a person can stop following.
- `messaging.activity (org_id, account_id, seq, kind, conversation_id, message_id, created_at)` is a personal feed per account in each cell. It records only events that concern that person directly: a reply in a followed thread, a mention, a new DM or group DM message, and read-state changes. Ordinary channel messages never go into it, so this is fan-out on write only for targeted events.
- The feed has its own commit-ordered `seq` per account. It is assigned the same way, under the lock of a per-account cursor row. Feed rows are written by a consumer of `messaging.message-posted.v1` (and its Phase 2 siblings), not in the send transaction, so sending stays fast. The outbox guarantees that no feed row is lost.
- Feed rows carry ids only, never content. Content is always read through the regular permission check, so a person removed from a conversation gets nothing from an old feed row.
- Feed rows are kept 30 days and deleted by a maintenance job (ADR 0010). Read state and thread followers are not time-limited.

### Sync: snapshot plus delta

- `GET /api/v1/orgs/{orgId}/sync?since={feedSeq}` returns the feed rows after the cursor (bounded pages) together with the conversations whose `last_seq` or read state changed. The client then fetches the open conversation's messages with `after={lastSeenSeq}`.
- If the cursor is older than the feed's retention, or unknown, the server answers that a full resync is needed. The client then takes a snapshot: the conversation list with unread counts and the followed threads with unread replies. Nothing is lost, because unread counts come from `read_states` and `last_seq`; the feed only speeds up "what changed".
- Clients keep their cursors per cell (each cell is synced separately), with the conversation list, in the local cache. That is IndexedDB on the web and SQLite on desktop, with a size limit and eviction. A client draws from the cache first and then syncs, which meets the start and switch budgets.

### Storage encryption

- There is no application-level encryption of message text or blocks. The cell's PostgreSQL stores them as plain values, so full-text search, ordering and previews work inside the database.
- Encryption at rest happens at the storage layer: the database volumes, object storage, the WAL archive and backups are encrypted with KMS-managed keys, and backups use a separate key. With BYOK the keys live in the customer's KMS. This is configured in the deploy phase and is part of the deployment checklist.
- Access to plaintext is limited by the other controls:
  - RLS with the organization context taken from authentication;
  - no API or admin screen that reads other people's messages;
  - application roles that are neither superusers nor owners;
  - the maintenance role, which cannot read content (ADR 0010);
  - no content in logs, traces or events;
  - audited operator access to the database.
- Search uses `ISearchIndex` with PostgreSQL full-text search inside the cell. A search module consumes the message events and maintains a `tsvector` with a GIN index. The text configuration is language-neutral, with unaccent and an explicit, culture-invariant case rule; Turkish İ/ı tests are mandatory. Results are filtered by the same membership and permission checks as reads, in the same query. The index never leaves the cell.

## Consequences

- Clients never miss a message or a thread reply after a reload, a new sign-in or a dropped connection. The worst case is a full resync, which is correct, only slower.
- Sends cost one row lock per conversation, and targeted events cost one feed row per concerned person. Channel size does not multiply writes.
- One more consumer (the activity feed writer) and one more maintenance job (feed retention).
- Everything in the cell can search the stored plaintext. Protecting it against someone with database or host access relies on operational controls and storage encryption, not on cryptography inside the application. This matches the requirements (server-side encryption, no end-to-end encryption). If a customer ever needs field-level encryption, it can be added behind the storage interfaces, at the cost of server-side search for that organization.
- The Phase 2 contract defines the API shapes, limits and live events for read state, followers, the feed and sync on this basis.
- Code for read state, the feed and sync touches privacy rules (what people can see) and needs human review.

## Implementation

- Phase 1 (done in step 6.1): the per-channel `seq`, `after`/`before` by `seq`, and `seq` in `MessagePosted`. Step 7, the web client: keep the last seen `seq` per channel and catch up after reconnecting.
- Phase 2:
  - conversations for DMs, group DMs, private channels and threads, with their own `seq`;
  - `read_states`;
  - `thread_followers`;
  - the activity feed, with its consumer and retention job;
  - the sync endpoint;
  - the search module.
- Deploy phase: storage, WAL and backup encryption with KMS, BYOK, and the operator access audit.
