# 0011. Message sync, read state and storage encryption

- Status: Proposed
- Date: 2026-11-24

## Context

People must never miss a message or a change to one. They refresh the page, sign in on another device after weeks away, lose the connection, or get a reply in a thread whose root is 150 messages back in a channel they already read. Live events (SignalR) can be lost during a reconnect, and a node can restart mid-delivery. So live delivery alone cannot tell a client what changed.

Phase 1 has public channels with plain-text messages that are never edited or deleted. Step 6.1 (PR #33) gives each channel a commit-ordered `seq` (decision 12 of the Phase 1 contract), and clients catch up with `GET .../messages?after={seq}`. Phase 2 adds DMs, group DMs, private channels, threads, mentions, edits, deletes, reactions, read state and unread counts across devices (requirements, "Mesajlaşma" and "Kanallar, DM'ler ve kenar çubuğu"). Their design must fit what Phase 1 builds now.

The requirements also fix the encryption model ("Şifreleme ve anahtarlar"). TLS protects data in transit. Encryption at rest is server-side, covering the database, object storage and backups. There is no end-to-end encryption, deliberately. Enterprise customers can bring their own key (BYOK). Search is PostgreSQL full-text search behind `ISearchIndex`, and it goes through the same permission check as regular reads.

## Decision

### Live events are hints; the server state is the truth

A client never derives correctness from live events. Every start, reload and reconnect asks the server what changed since the client's cursors. Live events only make that faster.

### One commit-ordered change counter per conversation

- **Conversations.** Channels (public and private), DMs, group DMs and threads are all conversations. A thread is its own conversation, rooted at a message of its channel.
- **The counter.** Every conversation has a `change_seq` counter. It is advanced in the changing transaction under the conversation row's lock (`UPDATE … SET change_seq = change_seq + 1 RETURNING change_seq`). The lock lasts until commit, so the order of the counter is the order of commits, and a cursor can never skip a change. Gaps are allowed: a lost race on an idempotent repeat leaves one, and a rollback leaves none.
- **What takes a number.** Every change takes the next number: a new message, an edit, a delete, a reaction change, a pin, and a root message's thread summary (reply count, last reply).
  - A message keeps `seq`, the number it took when it was created; `seq` gives the order of the conversation.
  - A message also keeps `version_seq`, the number of its latest change.
  - A deleted message stays as a tombstone for good: its content is erased, while `id`, `seq`, `version_seq` and `deleted_at` remain. Per-conversation cursors never expire, and message history is unlimited (requirements), so a tombstone of a few bytes is the simplest way to keep every cursor valid.
- **Countable messages.** Each conversation also keeps `last_message_seq`, the `seq` of its newest message. Unlike `change_seq`, it does not move on edits, reactions or pins, so it decides whether a conversation can have unread messages.
- **Catch-up.** `GET .../messages?changed_after={n}` returns every message whose `version_seq > n`: new ones, edited ones and tombstones, in `version_seq` order, through an index on `(conversation_id, version_seq)`. History paging (`before`/`after` by `seq`) stays as it is.
- **A thread reply on an old root.** The reply takes a number in the thread's counter. The update to the root's thread summary takes a number in the channel's counter. A client that synced the channel therefore sees the root change, even when the root is 150 messages back.
- **Phase 1.** Messages are insert-only, so `seq` is also the version: today's `after={seq}` is exactly `changed_after`. Phase 2 adds `version_seq`, tombstones and the `changed_after` name without changing the meaning of `seq`.
- **Cost.** The lock serializes changes within one conversation, about one change per commit time. That is ample for chat. A conversation that needs more is a broadcast feature with its own design.

### Cell epoch

- Each cell has an `epoch`, a value that changes whenever its database is restored or a replica is promoted. After an asynchronous failover, a promoted replica can hand out numbers that a client has already seen.
- The control plane's cell registry is the source of truth, because a value kept in the cell's own database would come back unchanged with a restore or a promoted replica. The failover and restore procedure increments the epoch in the control plane before the cell accepts writes. The cell receives it over the link when it connects, and its app role refuses writes until it has the current epoch.
- Every cursor the server returns carries the epoch. When a client presents another epoch, or a cursor ahead of the conversation's counter, the server answers that a full resync is needed.

### Read state on the server

- **The table.** `messaging.read_states (org_id, account_id, conversation_id, last_read_seq, version, updated_at)`.
- **Reading moves forward only.** Automatic reads (the conversation was shown) only move forward: `last_read_seq = GREATEST(last_read_seq, $seq)`, applied only while the row's `version` is the one the client last saw.
- **Explicit actions win.** An explicit "mark unread" or "mark read" sets the value unconditionally and bumps `version`, so an older automatic read from another device cannot undo it.
- **Version mismatch.** An automatic read that names a stale `version` changes nothing. The answer is 409 with the current read state, and the client adopts that state.
- **Own messages.** Sending a message advances the sender's own read state in the same transaction.
- **Unread counts.**
  - They count actual rows: messages after `last_read_seq` that are not deleted, not hidden from the person and not their own.
  - Counting is capped at 100 and shown as "99+".
  - Only conversations with `last_message_seq > last_read_seq` are counted, in one `LATERAL` query over the person's conversations.
  - The query uses a partial index on `(conversation_id, seq) WHERE deleted_at IS NULL`.
  - Sending a message writes no per-member rows, so the cost of a send does not grow with the size of the channel.
- **The sidebar.** It draws from the local cache first and then refreshes, so the 100 ms switch budget does not depend on this query.
- **Across devices.** Read state is stored on the server, applies on every device and is pushed to the person's other connections as a live event.

### Thread followers and a personal activity feed

- **Followers.** `messaging.thread_followers (org_id, thread_id, account_id)`. The thread's author, everyone who replies and everyone mentioned in it follow it automatically. A person can stop following.
- **The feed.** `messaging.activity (org_id, account_id, seq, kind, conversation_id, message_id, created_at)` is a personal feed per account in each cell.
  - It records only events that concern that person directly: a reply in a followed thread; a mention of the person; a new DM or group DM message; being added to or removed from a conversation; and read-state changes.
  - Ordinary channel messages never go into it.
  - Group mentions of everyone in a channel do not write one row per member. They are one row per conversation in a separate mention list, which members read through their membership.
    - Such a mention counts for a person only if it was sent after they joined, the conversation is not muted for them, and they have not turned off group mentions in their settings.
    - A mention of everyone online is resolved at send time, to the members whose presence is online at that moment. It writes one feed row each and is capped like other bulk notifications.
    - A named group is expanded to its members at send time, with the same rules.
- **How rows are written.**
  - The feed has its own commit-ordered `seq` per account, advanced under the lock of a per-account cursor row.
  - Rows are written by a consumer of the messaging events, not in the changing transaction, so changes stay fast. The outbox guarantees that no row is lost.
  - The consumer writes the rows of one event in batches, grouped by account, to keep lock time short.
- **Content and retention.** Rows carry ids only, never content, and content is always read through the regular permission check. Rows are kept 30 days and deleted by a maintenance job (ADR 0010). Read state and thread followers are not time-limited.

### Sync: snapshot plus delta, with a pending flag

- **The delta.** `GET /api/v1/orgs/{orgId}/sync?since={cursor}` returns, in bounded pages:
  - the feed rows after the cursor;
  - the conversations whose `change_seq` or read state changed.
- **Pending events.** Every change that will produce feed rows inserts a row into `messaging.feed_pending (org_id, event_id, created_at)` in its own transaction. The feed writer deletes that row in the transaction that writes the feed rows. The answer says `pending` while the organization has a `feed_pending` row created before the sync request began. Newer events cannot keep it set, so it clears as soon as the writer has caught up with what existed at the request. A client tells "caught up" from "consumer lag" by this flag and retries shortly, instead of assuming nothing happened.
- **Stuck events.** An event the writer cannot apply goes to the dead-letter queue, keeps its `feed_pending` row and raises an alert. Rows older than a few minutes no longer set `pending`, which would otherwise stay set; they set `degraded` instead. With `degraded` the client relies on the snapshot's unread counts and followed threads, which come from the source tables and are correct without the feed. Only the feed's extra hints, such as the time of the latest mention, may lag until the event is replayed.
- **Full resync.** It is needed when the cursor is older than the feed's retention, comes from another epoch, is ahead of the server, or is unknown. The snapshot is built from the source tables in one `REPEATABLE READ` transaction and comes with the feed cursor it is consistent with. It contains:
  - the person's conversations and memberships;
  - each conversation's `change_seq` and the person's read state and unread count;
  - mute, star and personal sidebar sections;
  - followed threads with unread replies;
  - the open mentions.
- **Removal from a conversation.** It appears in the feed and in the snapshot as a missing membership. The client then deletes that conversation's messages and files from its local cache (IndexedDB on the web, SQLite on desktop), so content does not stay readable on the device.
- **Client state.** Clients keep their cursors per cell (each cell is synced separately), with the epoch and the conversation list, in a local cache with a size limit and eviction. A client draws from the cache first and then syncs, which meets the start and switch budgets.

### Storage encryption

- **No application-level encryption.** Message text and blocks are not encrypted by the application. The cell's PostgreSQL stores them as plain values, so full-text search, ordering and previews work inside the database.
- **Encryption at rest is at the storage layer, with KMS-managed keys.** It covers every place content or personal data can land:
  - PostgreSQL volumes, including temporary files, the WAL archive and backups (backups use a separate key);
  - object storage;
  - RabbitMQ's disk, including dead-letter queues (events carry ids, but payloads such as `InvitationAccepted` carry personal data);
  - Valkey: live events carry message text through pub/sub, which is never persisted. Valkey also holds revoked sessions, so its persistence (RDB/AOF) is either off or on an encrypted volume;
  - crash dumps: off in production, or written to an encrypted volume;
  - the OpenTelemetry pipeline, which carries no content by rule and sends to an encrypted store.

  This is configured in the deploy phase and is part of the deployment checklist.
- **BYOK.**
  - Bringing your own key means a dedicated cell whose storage keys live in the customer's KMS. Shared cells use our keys.
  - Revoking the key makes the cell's storage unreadable. A running cell keeps working only as long as its decrypted volumes stay mounted, and it fails on the next restart or snapshot restore. This is documented for the customer as the intended outcome.
- **Client caches.** Desktop's SQLite cache lives in the app's data folder, which the operating system protects per user; full-disk encryption is the user's or administrator's choice. The web cache (IndexedDB) is cleared when the person signs out.
- **Who can reach plaintext.** It is limited by the other controls:
  - RLS, with the organization context taken from authentication;
  - no API or admin screen that reads other people's messages;
  - application roles that are neither superusers nor owners;
  - the maintenance role, which cannot read content (ADR 0010);
  - no content in logs, traces or events;
  - audited operator access to the database.

### Search

- **Where the index lives.** Messaging owns the search data, because events carry no text. It is a generated `tsvector` column on `messaging.messages` with a GIN index, behind `ISearchIndex`, so a later engine can replace it.
- **Text rules.** The text configuration is language-neutral, with unaccent and an explicit, culture-invariant case rule. Turkish İ/ı tests are mandatory.
- **Permissions.** Membership and read permission are checked when the query runs, in the same query as the text match. Counts, facets, ranking and snippets are computed only over rows that passed that filter, so neither a hit count nor a snippet reveals a conversation the person cannot read.
- **Location.** The index never leaves the cell.

## Consequences

- **No missed messages.** Clients never miss a message, an edit, a delete or a thread reply after a reload, a new sign-in, a dropped connection or a failover. The worst case is a full resync, which is correct, only slower.
- **Write cost.** Changes cost one row lock per conversation, and targeted events cost one feed row per concerned person. Channel size does not multiply writes.
- **New parts.** One more consumer (the activity feed writer), one more maintenance job (feed retention), tombstones for deleted messages, and an epoch per cell.
- **Plaintext in the database.** Everything in the cell can search the stored plaintext. Protecting it against someone with database or host access relies on operational controls and storage encryption, not on cryptography inside the application. This matches the requirements (server-side encryption, no end-to-end encryption). If a customer ever needs field-level encryption, it can be added behind the storage interfaces, at the cost of server-side search for that organization.
- **Phase 2 contract.** It defines the API shapes, limits and live events for read state, followers, the feed, sync and search on this basis.
- **Human review.** Code for read state, the feed, sync and search touches privacy rules (what people can see and find) and needs human review.

## Implementation

- **Phase 1.** Step 6.1 (PR #33) adds the per-channel `seq`, `after`/`before` by `seq`, and `seq` in `MessagePosted`. In step 7 the web client keeps the last seen `seq` per channel and catches up after reconnecting.
- **Phase 2:**
  - conversations for DMs, group DMs, private channels and threads;
  - `change_seq`, `last_message_seq` and `version_seq`, permanent tombstones and `changed_after`;
  - the cell epoch, held by the control plane;
  - `read_states` with forward-only automatic reads;
  - `thread_followers` and the group mention list;
  - the activity feed, with its consumer, `feed_pending` and retention job;
  - the sync endpoint and the snapshot;
  - search on `messaging.messages`.
- **Deploy phase:**
  - storage, WAL, temporary file and backup encryption with KMS;
  - RabbitMQ and Valkey storage settings;
  - crash dumps;
  - BYOK for dedicated cells;
  - the operator access audit.
