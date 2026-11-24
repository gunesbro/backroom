# Phase 2 contracts: messaging core

- Status: Proposed (needs the owner's approval before implementation)
- Date: 2026-11-24
- Requirements (living document, read 2026-11-24): "Yol haritası" (Faz 2), "Kanallar, DM'ler ve kenar çubuğu", "Mesajlaşma", "Varlık, durum ve çalışma saatleri", "Bildirimler", "Dosyalar, arama ve komut paleti", "Roller, izinler ve moderasyon", "Gizlilik ve güvenlik kuralları", "Performans bütçesi", "Akıcılık teknikleri", "Modül planı", "Ek: izin listesi"
- Builds on: [Phase 1 contract](phase-1.md), ADR [0005](../adr/0005-tenant-isolation-with-org-id-and-rls.md), [0006](../adr/0006-transactional-outbox-and-thin-rabbitmq-layer.md), [0010](../adr/0010-maintenance-role-for-cross-organization-jobs.md), [0011](../adr/0011-message-sync-read-state-and-storage-encryption.md); ADR 0012 (send path durability and performance) is **pending** and decides how sequence numbers are assigned (see "Sequence numbers")

## Goal and gate

The roadmap's Phase 2: DMs and group DMs, threads, replies, forwarding, reactions, read state, polls, scheduled messages, reminders; files, search, command palette, notifications, presence, audit log, moderation. Private channels, the rich text editor and the sidebar (sections, All DMs) come with them, because Phase 1 left them out and the Phase 2 features need them.

**Gate:** an end-to-end test on two cells and two clients per person passes these scenarios, and the Phase 2 k6 test meets the budgets below at 5,000 concurrent users:

1. Alice and Bob exchange DMs; Carol (Admin) can see neither the DM nor its existence. A group DM of three is converted to a private channel; Carol sees that a private channel exists and its member count, never its name or content, in no API, live event, search result, notification or audit entry.
2. A reply in a thread whose root is 150 messages back reaches Bob's second device after it was offline; edits, deletes and reactions made meanwhile are present after sync; unread counts and the "new" line match on both devices; "mark unread" on one device wins over an older automatic read on the other.
3. A file uploaded in a private channel downloads through a short-lived signed URL; after the person is removed from the channel the next download is refused and the local cache drops the channel.
4. Search finds `İstanbul` for `istanbul`, `ISTANBUL` and `ıstanbul`, only in conversations the searcher can read, and hit counts do not change when unreadable matches exist.
5. A mention reaches Bob as a notification; with "Do not disturb" on it lands in the bell silently.
6. An anonymous poll never reveals who voted for what, and the mapping is gone after it closes.
7. A scheduled message is sent at its time; a reminder fires at its time.
8. A reported message reaches moderators as a copy; the report is in the audit log without content.

**Not in Phase 2:** link previews and integrations (Phase 5, open question 3), work hours, leave and status history (Phase 4; Phase 2 uses the default 22:00–07:00 off-hours window), rooms and meetings in the command palette and the bell's Meetings and Rooms tabs (Phase 3), role and permission screens (Phase 5), plans, quotas and Licensing (open question 4), push notifications while no app is running (open question 6), data export and account deletion, custom emoji, message retention settings, mobile.

## Sequence numbers (ADR 0011, pending ADR 0012)

ADR 0011 fixes the observable guarantees; ADR 0012 decides the mechanism (the row lock of ADR 0011 and Phase 1 decision 12, or an alternative) and the send path's durability. This contract depends only on these guarantees:

- Every conversation has `change_seq` (every change takes the next number), `last_message_seq` (the newest message's `seq`), and every message has `seq` (creation order) and `version_seq` (its latest change).
- Numbers are positive 64-bit integers, increasing in commit order within one conversation; gaps are allowed; a cursor never skips a committed change.
- Clients treat them as opaque per-conversation cursors and never assume density.

Whatever ADR 0012 decides is written into this section before step 2A.2 is implemented.

## Flows

### 1. Conversations, membership and the sidebar

- **Kinds.** `public_channel`, `private_channel`, `dm`, `group_dm`, `thread` are all conversations (ADR 0011). Phase 1's `channels` become `conversations` (step 2A.1).
- **Public channels.** Anyone with `channels.create-public` creates one. Every active member can read it, search it and join it; joined channels appear in the sidebar and count unread. Leaving is always allowed. Rename, description (at most 250 characters, `DisplayText` rules without the visible-character requirement) and archive need `channels.manage-public`. Archived conversations are read-only and stay readable and searchable.
- **Private channels.** Created with `channels.create-private`, shown with a lock. Only members see them (name, content, members). The creator is the owner: removes members, renames, archives, transfers ownership. When the owner leaves, ownership passes to the member who joined earliest (ties: lowest account id). Every member can add active organization members (at most 50 per call). Admins and the Owner role, without membership, see only that it exists and its member count (open question 11 for where). A private channel cannot become public.
- **DMs.** `POST .../dms { accountIds }` with one other active member opens the DM, creating it on first use (one per pair, unique key). A DM with a deactivated member stays readable and refuses new messages (`409 messaging.conversation_read_only`). A DM with oneself is not in Phase 2.
- **Group DMs.** 3 to 9 participants including the creator (open question 7). No name; shown by participants' names. The same set of people opens the existing group (unique key over the sorted set while nobody has left). Anyone can leave; nobody can be removed. Adding a person creates a new group without the old messages. "Özel kanala dönüştür" converts the group in place into a private channel (name required, `channels.create-private`, the converter becomes owner, history kept, members unchanged).
- **Threads.** A thread is created by the first reply to a root message of a channel, DM or group DM (not of a thread, a poll or a deleted message). Read access is the parent's. "Kanala da gönder" also posts a reference row in the parent conversation that shows the reply (edits and delete reflect).
- **Removal.** Being removed from a conversation or the organization, or leaving, removes read access at once (next request), removes the person's connections from the conversation's live group on every node, and appears in sync as a missing membership, after which the client deletes that conversation's messages and files from its local cache (ADR 0011).
- **Sidebar (personal).** Sections are personal and private (nobody else sees them): create, rename, delete, order, collapse; drag a conversation into a section; per section "Göster" (all, unread only), "Sırala" (recent activity, name A–Z), "Kenar çubuğunda en fazla" (5 for DMs by default; 10, 15, 20, all). Unread and starred conversations always show regardless of the limit and of collapsing; muted ones are faded and sort last. Per conversation: star, mute (1 hour, 8 hours, until tomorrow 09:00 in the person's time zone, until I unmute), hide (until a new message arrives), move to section. Fixed entries Threads and Saved sit at the top; Rooms and Calendar stay "Yakında".
- **All DMs.** Search by participant name and filters Tümü, Okunmamış, Gruplar, Kişiler, Yıldızlı, Sessizde; each row shows the last message preview (at most 120 characters, read under membership) and offers star, mark read, mute, hide.

### 2. Messages

- **Rich text.** Formatting toolbar ("Aa"), bold, italic, strike, link, bulleted and numbered lists, quote, inline code, code block, mentions. Enter sends, Shift+Enter adds a line; the send button is disabled while empty; the thread composer is smaller and starts with the toolbar closed. The editor library is open question 1.
- **Input and storage.** The client sends `blocks` in our own JSON schema (Phase 1 decision 12), never editor-native or HTML. The server validates them against a closed schema (node types, marks, nesting depth at most 4, at most 32 KB of JSON, `href` only `https`, `http` or `mailto` and at most 2,048 characters, text nodes under Phase 1's text rules), drops nothing silently (`400 messaging.message_invalid` with a `reason`), re-serializes them canonically and derives `text` (1 to 4,000 characters as in Phase 1, after derivation) and the mention and link rows. Unknown node types are refused on write and skipped on read.
- **Mentions.** Inline `mention` nodes name an `accountId`, a `groupId`, or `everyone` (`@kanal`) or `here` (`@burada`). Accounts must be active organization members and groups must exist (`400 messaging.mention_invalid`); `everyone`/`here` need `messages.mention-everyone`, groups `messages.mention-groups` (`403 permissions.forbidden`). At most 50 mentions per message. A mentioned person who cannot read the conversation gets nothing; the client offers to add them ("kanala ekle?") before sending, never the server. Group mentions follow ADR 0011: `everyone` is one row in the group mention list; `here` and named groups are expanded at send time (`here` to members online at that moment, capped at 1,000 feed rows; larger groups count only through the list).
- **Reply with quote.** `replyToMessageId` names a message of the same conversation; its author is notified. If the original is deleted the quote shows "Bu mesaj silindi".
- **Edit and delete.** Unlimited in time, by the author only. Edits show "düzenlendi"; no edit history is kept and a delete erases text, blocks, mentions, links, reactions, pins, saved entries and attachments at once, leaving the tombstone of ADR 0011 ("Bu mesaj silindi"); this follows the requirements' proposal (open question 2). Moderators with `messages.remove-others-public` delete others' messages in public channels only. Deleting a thread root keeps its thread under a tombstone.
- **Forward.** Within the organization, to at most 10 targets per call (conversations the forwarder can post to, or accounts, which open DMs), with an optional note. The forwarder must be able to read the original. A copy is a message whose content is the original's current content (a forward of a forward points to the original); edits and the delete reflect in copies ("İletilen mesaj, sahibi tarafından silindi"). "Orijinal mesaja git" shows only to viewers who can read the original; from a private channel the copy shows "Özel kanal" and no link, and from a DM or group DM "Doğrudan mesaj" and no link. Attachments are shared with the copy's readers. At most 100 copies per original (`409 messaging.forward_limit`). The confirmation names how many people will see it ("# duyurular'daki 120 kişi bu mesajı görecek. Ekli dosya da paylaşılacak.").
- **Reactions.** One Unicode emoji sequence (at most 32 bytes, validated against an emoji list shipped with the server) per reaction; at most 50 distinct emoji per message and 20 per person per message (`409 messaging.reaction_limit`). Messages carry a summary `{ emoji, count, reactedByMe, sample: [accountId ×3] }`.
- **Pins and saved messages.** Pins need `messages.pin`, at most 100 per conversation; shown in the conversation's Pinned tab. Saving is personal: at most 5,000 saved items per person, with an optional reminder.
- **Seen.** In DMs and group DMs the author sees per person who has seen a message; in channels, a count ("Görüldü · 24"). It is based on `seen_seq`, which only moves forward and is not moved back by "mark unread" (requirements: marking does not take back "seen"). Only the author of the message can ask.
- **Typing.** A hub call, at most one per 3 seconds per person and conversation, delivered to the conversation's group (except the sender) and never stored.
- **Long text (Phase 1 decision 12).** Over 4,000 characters the composer offers to send the text as a `.txt` attachment (UTF-8, named after the first words, at most 60 characters plus `.txt`, with an optional short message) through flow 6; the server still never accepts more than 4,000 characters in `text`.
- **Rate limits.** Per account and organization: 20 sends per 10 seconds, 30 reactions per 10 seconds, 10 forwards per minute (`429 messaging.rate_limited` with `Retry-After`).

### 3. Read state, unread and sync (ADR 0011)

- **Read state.** As ADR 0011: automatic reads move forward only and only with the current `version`; explicit "mark read"/"mark unread" (Esc for a conversation, Shift+Esc for all, Alt+click for a message) set the value and bump `version`; a stale automatic read answers `409 messaging.read_state_stale` with the current state; sending advances the sender's own read state. Threads have their own read state. Marking affects only the person's view and syncs across devices.
- **Unread.** Counted from rows, capped at 100 and shown as "99+". The banner "2 yeni mesaj · 10:12'den beri · Okundu say · İlk okunmamışa git" and the "Yeni" line come from `lastReadSeq` as loaded when the conversation opened.
- **Thread followers and the activity feed.** As ADR 0011 (followers: root author, repliers, mentioned people; a person can stop following; feed rows with ids only, kept 30 days). Feed kinds: `thread_reply`, `mention`, `group_mention` (here and groups), `reply` (quoted), `dm_message`, `conversation_added`, `conversation_removed`, `read_state`, `reminder_due`, `scheduled_failed`, `invitation_accepted`.
- **Sync.** `GET .../sync?since={cursor}` returns the delta in pages; `GET .../sync/snapshot` the full state in pages from one `REPEATABLE READ` snapshot, with the cursor it is consistent with. `pending` and `degraded` as ADR 0011. A cursor that is too old, of another epoch, ahead of the server or malformed answers `409 messaging.resync_required`. Cursors are opaque strings (base64url of epoch and feed `seq`), at most 64 characters.
- **Cell epoch.** The control plane's cell registry holds `epoch`; `Welcome` carries it (new field, additive). The connector stores it; the app role refuses writes with `503 cell.epoch_unconfirmed` (`Retry-After`) while the stored epoch is not confirmed. The restore and failover procedure increments the epoch in the control plane and clears the confirmation in the restored database before the cell starts. _(human review)_
- **Client.** Per cell: cursors, epoch, conversation list and recent messages in a local cache (web: IndexedDB; desktop: open question 9) of at most 200 MB, evicting the least recently opened conversations first and keeping at most the newest 1,000 messages per conversation; cleared on sign-out and on removal. Start and switch draw from the cache, then sync. After every reconnect, `sync?since` runs before relying on live events again.

### 4. Polls

- Created from the composer's "+" menu: question (1 to 300 characters), 2 to 10 options (1 to 100 characters each, reorderable), "Birden fazla seçim", "Anonim oylama", "Sonuçlar" (always visible, after voting, after closing; default after voting), "Bitiş" (optional, from 5 minutes to 90 days ahead). A poll is a message of kind `poll`; it cannot have a thread, be edited or be forwarded.
- Votes can be changed while open; the creator can close early; options cannot change after the first vote (a poll is never edited, so this always holds). A scheduled job closes polls at `closes_at`.
- **Anonymous polls.** The vote-to-person rows exist only to enforce one vote per person and allow changing it. No API returns them to anyone, the creator and admins included; the voter's own choice is returned only to the voter (open question 10). Closing deletes every vote row of the poll in the same transaction and keeps only the counts.
- Non-anonymous polls show voters per option to readers of the conversation.
- Live events carry no counts (`PollChanged { messageId, versionSeq }`), because visibility depends on the viewer; clients read the poll, and the server applies results visibility per viewer.
- Display: "12 kişi oy verdi · Yarın 17:00'de kapanır", "Oyumu değiştir", "Sonuçlandı", "Anonim", "14 oy · Dün 17:00'de kapandı".

### 5. Scheduled messages and reminders

- **Scheduled messages.** Same body as a send (blocks, attachments, reply, thread), at a time from 1 minute to 120 days ahead; at most 100 pending per person. Editable until sent, "send now", delete. Only the author sees them (list "Zamanlanmış mesajlar"). At the time a worker job sends it through the regular send path as the author, rechecking membership and permissions; when that fails the author gets a `scheduled_failed` feed entry and the draft stays for 7 days. Once sent it is a normal message.
- **Off-hours warning.** When the recipient of a DM (or every other person in a group DM) is outside working hours (Phase 2: 22:00–07:00 in their time zone), the composer warns with their local time and offers "mesai başlangıcında gönder" (07:00 their time). The server answers `GET .../dms/{id}/recipient-hours` with only `{ outsideHours, localTime, nextStart }` per person, never the time zone.
- **Reminders.** On a saved message: 20 minutes, 1 hour, tomorrow 09:00, next week (Monday 09:00), or a custom time up to one year ahead, in the person's time zone. When due, a `reminder_due` feed row and a notification; reminders sit in Saved's "Hatırlatıcılar" tab. A reminder on a message the person can no longer read is dropped silently.
- Jobs store their schedule in PostgreSQL (ADR 0006), run in the worker, select due rows with `FOR UPDATE SKIP LOCKED` through a partial index on pending rows, and use `TimeProvider`.

### 6. Files

1. **Start.** `POST .../files/uploads { conversationId, name, size, contentType }` with `files.upload` and the right to post in the conversation. Rules: size 1 byte to 100 MB (`413 files.too_large`); name NFC, at most 255 characters, no path separators or control characters (`400 files.name_invalid`); content type from an allow-list or `application/octet-stream`. Answers `{ fileId, uploadUrl, headers, expiresAt }`: a presigned PUT valid 15 minutes for exactly that size and content type, to a storage key the server chose (`{orgId}/{fileId}`; the name never appears in keys or URLs).
2. **Upload.** The client uploads straight to object storage through `IFileStorage` (SeaweedFS in development).
3. **Complete.** `POST .../files/{fileId}/complete`: the server checks the object's size, sniffs the type from the first bytes and stores the detected type and a kind (`document`, `image`, `spreadsheet`, `archive`, `other`). Images (at most 50 megapixels) get thumbnails in the worker (64 and 360 px wide, re-encoded WebP); the file is usable before its thumbnails exist.
4. **Attach.** A send names at most 10 ready, unattached files of the same uploader and conversation (`400 messaging.attachment_invalid`). Unattached uploads are deleted after 24 hours by a maintenance job.
5. **Download.** `GET .../files/{fileId}/download` (and `.../thumbnails/{size}`) rechecks access on every request (Messaging: the person can read a message, or a forwarded copy, that carries the file) and answers `302` to a signed GET URL valid 60 seconds with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff` (thumbnails inline, `image/webp`). The signed URL is never cached or logged. Refused and unknown both answer `404 files.not_found`.
6. **Delete.** The uploader, or `files.remove-others-public` in public channels. Deleting the message deletes its attachments. Objects are deleted by a job within an hour.
7. **Conversation tabs.** Files (filters Belgeler, Görseller, Tablolar and "Paylaşan"), Links (from the link rows of messages), Pinned. Each list is keyset-paged, newest first, showing thumbnails only.

Upload limits per account: 30 starts per minute and 2 GB per hour (`429 files.rate_limited`). Malware scanning is open question 5; until then files are never served inline and never from our web origin.

### 7. Search and the command palette

- **Scope.** Messages (Messaging, ADR 0011), files by name (Files), channels by name (Messaging: public channels and private channels the person belongs to), people by display name (Organization). Rooms come in Phase 3.
- **Query.** `GET .../search?q=&type=&from=&in=&after=&before=&has=&mentionsMe=&threads=&cursor=&limit=`. Typed filters ("kimden:", "nerede:", "tarih:", "dosya:pdf", and their English forms) are parsed by the client into these parameters, so the server never parses localized syntax. Quick filters: has file, has link, mentions me, threads. `q` at most 200 characters and 10 terms. Results newest first, keyset by `(created_at, id)`, 20 per page. Tab counts are capped at 100.
- **Text rules.** NFKC, unaccent, then a fixed fold applied to both the index and the query: `İ`, `I`, `ı` and `i` all fold to `i`, and every other letter is lower-cased with the invariant rule. Prefix matching on the last term. The `simple` text search configuration (no language stemming).
- **Permissions.** As ADR 0011: membership and read access are part of the same SQL query as the match; counts and snippets are computed only over rows that passed. Snippets are returned as plain text with highlight offsets, never markup.
- **Command palette (Ctrl+K).** Client-side commands plus `GET .../search/quick?q=` (channels and people, at most 8 each, prefix match on names). Inaccessible conversations never appear.
- Rate limit: 10 searches per 10 seconds per account (`429 search.rate_limited`).

### 8. Notifications

- **Inputs.** Feed rows for the person (mentions, quoted replies, thread replies, DM messages, additions to conversations, reminders, failed scheduled messages) and the invitation acceptance promised in Phase 1 (to the inviter). Messaging publishes `messaging.activity-added.v1`; Organization publishes acceptance; Notifications decides.
- **Decision order (requirements).** Do not disturb (and, from Phase 4, leave and off-hours) first; then the conversation setting (all messages; mentions and replies; nothing; the unread badge stays); then organization mute; then the account default. Extra settings per conversation: replies in followed threads, ignore `@kanal` and `@burada`, sound. Mute options as the sidebar. Texts as the Thread design ("Varsayılanlar Hesap ayarları › Bildirimler'den gelir. Bu ayar yalnızca # genel için geçerlidir.").
- **Outputs.** A bell entry (tabs Tümü and Bahsedilmeler in Phase 2; "Tümünü okundu say"), and, unless suppressed, a `NotificationRequested` live event to the person's account group, which the web app shows with the browser's Notification API and the desktop app with the operating system's notifications (the desktop app keeps running in the tray when its window closes). Under do not disturb, entries collect in the bell silently ("Rahatsız etme açıkken bildirimler burada birikir.").
- **Content.** Bell rows store ids only; the preview is read through the regular permission check when shown. The live event carries a preview of at most 150 characters, only to that person; an account setting hides previews in operating system notifications.
- **Never** for a conversation the person cannot read, never for their own messages, at most one notification per message per person.

### 9. Presence and status (Phase 2 base)

- **Automatic presence.** Online, idle (10 minutes without input, reported by the client), offline (no connection to the cell for 60 seconds). Computed per cell from the person's hub connections and kept in Valkey with expiry; never stored in PostgreSQL in Phase 2.
- **Manual status.** Do not disturb (30 minutes, 1 hour, until tomorrow, custom) and a custom status (one emoji, at most 100 characters, optional expiry) are account-level, so they look the same in every organization. They live in the control plane (Identity), are set with `PUT /api/v1/me/status`, and reach every cell where the account is a member through the link (`AccountStatusChanged`, versioned). The time zone (IANA id reported by the client) travels the same way and is used only for reminders, mute "until tomorrow" and the off-hours warning; no endpoint returns it to others.
- **Activity labels** ("Toplantıda", "Ekran paylaşıyor") arrive with meetings in Phase 3.
- **Watching.** A client asks for presence of the people it shows (at most 200 per connection, through the hub) and gets changes through per-person presence groups; changes are coalesced to at most one per 5 seconds per person. Presence never shows platform or device.

### 10. Audit log and moderation

- **Audit log.** Append-only (`INSERT` and `SELECT` only for the app role; no update or delete path) and organization-scoped. Phase 2 records: role assignments; member added, removed, deactivated, reactivated; invitation created, resent, revoked, accepted, failed (Phase 1 promise); public channel created, renamed, archived, unarchived (with names); private channel created, archived, ownership transferred (ids and member counts, **never names**); DMs and group DMs not at all; messages removed by moderators (ids only); reports filed and resolved; groups changed. Login and security events of the control plane stay security log events until Phase 5. No content in any entry. Viewing needs `audit.view`; keyset-paged, newest first, filter by action and actor.
- **Reporting.** "Mesajı bildir" by anyone who can read the message: reason (spam, harassment, inappropriate, other) and an optional note (at most 500 characters); at most 20 reports per person per day and one open report per person and message. The report stores a copy of the message (text, blocks, attachment names, author, time, and the conversation kind; the conversation name only for public channels), which goes to people with `moderation.review-reports`. The reporter chose to share it; nobody else's messages are copied.
- **Review.** Moderators list open reports, dismiss one, or act: delete the message (public channels only), remove or deactivate the author (flow 11). Every resolution is audited. The copy is deleted 90 days after resolution (open question 8).
- **Delivery status of invitations** (Phase 1 promise): the invitation list gains `deliveryStatus` (`pending`, `sent`, `failed`, `unknown`) from Email's delivery log.

### 11. Member removal, deactivation and user groups

- **Remove** (`members.remove`): the person leaves the organization, loses every conversation membership, and may return by a new invitation; their messages stay with their name. **Deactivate** (`members.deactivate`; reactivate likewise): the person cannot return by invitation, their live connections to the organization end at once, their messages stay labelled "deaktif", DMs with them become read-only. Owner cannot be removed or deactivated; nobody acts on a role higher than their own (requirements' proposal). Both publish `organization.member-removed.v1` or `organization.member-deactivated.v1`, revoke their pending invitations in the same transaction (Phase 1 flow 3 step 8) and update the control plane through `MembershipChanged`. _(human review)_
- **User groups** (`members.manage-groups`): name (`DisplayText`, at most 80, unique per organization), at most 1,000 members, at most 500 groups per organization. Phase 2 uses them only for group mentions ("@tasarım-ekibi"); floor access and calendar scopes come later.

## Modules

| Module                                                                             | Runs in       | Schema                    | Phase 2 scope                                                                                                                                                                                                          |
| ---------------------------------------------------------------------------------- | ------------- | ------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Messaging](../../backend/src/Modules/Messaging/CLAUDE.md)                         | Cell          | `messaging`               | Conversations and membership, rich messages, mentions, threads, replies, forwards, reactions, pins, saved and reminders, read state, followers, feed, sync, polls, scheduled messages, search of messages and channels |
| [Realtime](../../backend/src/Modules/Realtime/CLAUDE.md)                           | Cell (app)    | – (Valkey)                | Conversation, account and presence groups; typing; removal from conversation groups; presence connections                                                                                                              |
| Files (new)                                                                        | Cell          | `files`                   | Uploads, completion, thumbnails, signed downloads, deletion, file lists, file name search                                                                                                                              |
| Notifications (new)                                                                | Cell          | `notifications`           | Preferences, decision order, bell, notification live events                                                                                                                                                            |
| Presence (new)                                                                     | Cell          | `presence`                | Automatic presence, copy of account status and time zone, `IPresenceReader`, off-hours check                                                                                                                           |
| Audit (new; "Denetim ve moderasyon")                                               | Cell          | `audit`                   | Audit log, reports and their review                                                                                                                                                                                    |
| [Organization](../../backend/src/Modules/Organization/CLAUDE.md)                   | Cell          | `organization`            | Remove, deactivate, reactivate; user groups; people search; inviter notification event                                                                                                                                 |
| [Permissions](../../backend/src/Modules/Permissions/CLAUDE.md)                     | Cell          | `permissions`             | New permission keys and defaults                                                                                                                                                                                       |
| [Email](../../backend/src/Modules/Email/CLAUDE.md)                                 | Cell (worker) | `email`                   | Delivery status read for invitations (`IDeliveryStatus`)                                                                                                                                                               |
| [Identity](../../backend/src/Modules/Identity/CLAUDE.md)                           | Control plane | `identity`                | Account status and time zone, `AccountStatusChanged` fan-out                                                                                                                                                           |
| [CellManagement](../../backend/src/Modules/CellManagement/CLAUDE.md)               | Control plane | `cell_management`         | Cell `epoch`, sent in `Welcome`                                                                                                                                                                                        |
| [ControlPlaneConnector](../../backend/src/Modules/ControlPlaneConnector/CLAUDE.md) | Cell (worker) | `control_plane_connector` | Epoch storage and confirmation; `AccountStatusChanged` to Presence                                                                                                                                                     |

**Dependencies** (Contracts only): Messaging → Permissions, Organization (members, groups), Files (`IFileAttachments`: claim and release attachments), Presence (`IPresenceReader` for `@burada`), Realtime. Files → Messaging (`IMessageFileAccess`: may this account read a message carrying this file). Notifications → Messaging (read access, mute), Presence (do not disturb), Realtime. Search across modules is composed by Messaging's search endpoint calling `IFileSearch` (Files) and `IMemberSearch` (Organization) with the caller's account; each applies its own permission filter. Audit consumes events only. No module reads another's tables.

## Data (Phase 2 tables)

Columns are indicative; migrations are reviewed in each module's PR. Every table has `org_id` and forced RLS (Phase 1 cross-cutting rules). Personal tables (`account_id` first in their keys) are additionally filtered by the authenticated account in every query, and tests prove one person cannot reach another's rows.

Messaging:

- `conversations (id, org_id, kind, name, name_key, description, owner_account_id, parent_conversation_id, root_message_id, pair_key, created_by, created_at, archived_at, change_seq, last_message_seq, last_message_at, member_count)`; renamed from `channels`, `last_seq` becomes `change_seq` and `last_message_seq`; unique `(org_id, name_key)` among open channels, `(org_id, pair_key)` for DMs and unchanged group DMs, `(parent_conversation_id, root_message_id)` for threads
- `conversation_members (org_id, conversation_id, account_id, role owner|member, joined_at, joined_seq, added_by, left_at)`; index `(org_id, account_id)` for the person's conversations; at most 10,000 open memberships per person (`409 messaging.membership_limit`)
- `messages (id, org_id, conversation_id, seq, version_seq, kind text|poll|thread_broadcast, author_account_id, client_message_id, text, blocks, reply_to_message_id, forward_of_message_id, forward_note_blocks, broadcast_of_message_id, thread_id, reply_count, last_reply_at, reaction_summary, created_at, edited_at, deleted_at, search_vector)`; indexes `(conversation_id, seq)`, `(conversation_id, version_seq)`, partial `(conversation_id, seq) WHERE deleted_at IS NULL`, GIN on `search_vector`, `(forward_of_message_id)`
- `message_mentions (org_id, message_id, conversation_id, account_id)`, `group_mentions (org_id, conversation_id, message_id, seq, kind everyone|here|group, group_id)`, `message_links (org_id, conversation_id, message_id, url, host, created_at)`, `message_attachments (org_id, message_id, file_id, position)`
- `reactions (org_id, message_id, account_id, emoji, created_at)`, `pins (org_id, conversation_id, message_id, pinned_by, pinned_at)`
- `read_states (org_id, account_id, conversation_id, last_read_seq, seen_seq, version, updated_at)`; index `(conversation_id, seen_seq)` for seen counts
- `thread_followers (org_id, thread_id, account_id, unfollowed)`, `activity (org_id, account_id, seq, kind, conversation_id, message_id, actor_account_id, created_at)`, `activity_cursors (org_id, account_id, last_seq)`, `feed_pending (org_id, event_id, created_at)`
- `conversation_prefs (org_id, account_id, conversation_id, section_id, starred, hidden_until_seq, muted_until)`, `sidebar_sections (org_id, account_id, id, name, position, collapsed, show, sort, max_items)`; at most 50 sections per person
- `saved_items (org_id, account_id, message_id, remind_at, reminded_at, created_at)`
- `polls (org_id, message_id, question, multiple, anonymous, results_visibility, closes_at, closed_at)`, `poll_options (org_id, message_id, option_id, position, text, vote_count)`, `poll_votes (org_id, message_id, account_id, option_id, created_at)` (anonymous polls: rows deleted at close)
- `scheduled_messages (id, org_id, account_id, conversation_id, thread_root_message_id, reply_to_message_id, blocks, attachment_ids, send_at, status pending|sent|failed|cancelled, failure_code, sent_message_id, created_at, updated_at)`; content deleted when sent or 7 days after failure
- `outbox`, `inbox`

Files: `files (id, org_id, uploader_account_id, conversation_id, name, name_fold, size, declared_type, detected_type, kind, storage_key, status pending|ready|attached|deleted, width, height, created_at, ready_at, deleted_at)`, `thumbnails (org_id, file_id, width_key, storage_key, width, height)`, `outbox`, `inbox`.

Notifications: `conversation_settings (org_id, account_id, conversation_id, level all|mentions|none, followed_thread_replies, ignore_group_mentions, sound)`, `account_settings (org_id, account_id, default_level, org_muted_until, sound, hide_previews)`, `inbox_items (org_id, account_id, id, kind, conversation_id, message_id, actor_account_id, created_at, read_at)` (ids only, kept 30 days), `inbox`.

Presence: `account_status (account_id, dnd_until, custom_emoji, custom_text, custom_expires_at, time_zone, version)` (one row per account and cell, mirrored from the control plane; not organization data, so not RLS-scoped, and readable only through `IPresenceReader`), live state in Valkey.

Audit: `entries (id, org_id, occurred_at, actor_account_id, action, target_type, target_id, details)` (app role: insert and select only; `details` from a typed allow-list, never content), `reports (id, org_id, message_id, conversation_id, conversation_kind, reporter_account_id, author_account_id, reason, note, snapshot, status open|dismissed|actioned, resolved_by, resolved_at, created_at)`, `inbox`.

Organization: `groups (id, org_id, name, name_key, created_by, created_at)`, `group_members (org_id, group_id, account_id)`; `members.status` gains `removed` and `deactivated` with `deactivated_at`, `deactivated_by`.

Control plane: `identity.account_status (account_id, dnd_until, custom_emoji, custom_text, custom_expires_at, time_zone, version, updated_at)`; `cell_management.cells.epoch`.

Retention jobs (ADR 0010, maintenance role, no content columns readable): feed rows after 30 days; bell items after 30 days; unattached uploads after 24 hours; deleted file objects; failed scheduled drafts after 7 days; resolved report snapshots after 90 days.

## Integration events (RabbitMQ, inside a cell)

Ids only, never text, names or file names. Every event carries `event_id`, `occurred_at`, `org_id` and the trace context.

| Event                                                                | Publisher             | Consumers                                                                                       | Payload                                                                                                                     |
| -------------------------------------------------------------------- | --------------------- | ----------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| `messaging.message-posted.v1` (extended, additive)                   | Messaging             | Messaging feed writer                                                                           | `conversation_id, conversation_kind, message_id, seq, thread_id, author_account_id, reply_to_author_account_id, created_at` |
| `messaging.message-edited.v1`                                        | Messaging             | Messaging (forward copies, feed for new mentions)                                               | `conversation_id, message_id, version_seq, added_mention_account_ids`                                                       |
| `messaging.message-deleted.v1`                                       | Messaging             | Messaging (copies), Files (attachments), Audit (only when deleted by a moderator: `removed_by`) | `conversation_id, message_id, removed_by?`                                                                                  |
| `messaging.conversation-created.v1`                                  | Messaging             | Audit                                                                                           | `conversation_id, kind, created_by` (Audit records only channel kinds)                                                      |
| `messaging.conversation-changed.v1`                                  | Messaging             | Audit                                                                                           | `conversation_id, kind, change renamed                                                                                      | archived | unarchived | owner_transferred | converted, actor_account_id, member_count` |
| `messaging.conversation-member-added.v1` / `-removed.v1`             | Messaging             | Messaging feed writer, Realtime (group removal)                                                 | `conversation_id, account_id, actor_account_id`                                                                             |
| `messaging.activity-added.v1`                                        | Messaging feed writer | Notifications                                                                                   | `kind, conversation_id, message_id, actor_account_id, account_ids` (at most 500 per event)                                  |
| `messaging.reminder-due.v1`, `messaging.scheduled-message-failed.v1` | Messaging             | Messaging feed writer                                                                           | `account_id, message_id or scheduled_id`                                                                                    |
| `files.file-deleted.v1`                                              | Files                 | Messaging (attachment rows)                                                                     | `file_id, message_id?`                                                                                                      |
| `organization.member-removed.v1` (now published)                     | Organization          | ControlPlaneConnector, Realtime, Messaging (memberships), Notifications, Audit                  | as Phase 1                                                                                                                  |
| `organization.member-deactivated.v1` / `-reactivated.v1`             | Organization          | ControlPlaneConnector, Realtime, Messaging, Audit                                               | `account_id, actor_account_id, version`                                                                                     |
| `organization.invitation-accepted.v1`                                | Organization          | Messaging feed writer (inviter), Audit                                                          | `invitation_id, inviter_account_id, account_id`                                                                             |
| `organization.group-changed.v1`                                      | Organization          | Audit                                                                                           | `group_id, change, actor_account_id`                                                                                        |
| `permissions.role-assigned.v1`                                       | Permissions           | Audit                                                                                           | `account_id, role_id, actor_account_id`                                                                                     |
| `audit.report-filed.v1` / `-resolved.v1`                             | Audit                 | Notifications (moderators)                                                                      | `report_id, resolution?`                                                                                                    |

Removal from a conversation also goes to Realtime directly after commit (below); the event is the second, durable path.

## Live events (Valkey → SignalR)

Groups (all prefixed `cell:{cellId}:org:{orgId}:`): `conversation:{id}` (replaces `channel:{id}`; thread events go to the parent conversation's group, so a thread needs no subscription of its own), `account:{accountId}` (joined automatically by `SubscribeOrganization`, only that account's connections), `presence:{accountId}`, and the organization group for content-free events about public channels. Payloads are MessagePack maps with camelCase keys (Phase 1 decision 13); changed payloads get a new event name (`MessagePosted.v2`).

| Server → client                                          | Group                               | Payload                                                                                                                                                                                                                       |
| -------------------------------------------------------- | ----------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MessagePosted.v2`                                       | conversation                        | `orgId, conversationId, threadId?, message { id, seq, versionSeq, kind, clientMessageId, authorAccountId, blocks, text, replyTo?, forward?, attachments [{ fileId, name, size, kind, hasThumbnail }], createdAt }, changeSeq` |
| `MessageEdited`                                          | conversation                        | `orgId, conversationId, messageId, versionSeq, changeSeq, blocks, text, editedAt`                                                                                                                                             |
| `MessageDeleted`                                         | conversation                        | `orgId, conversationId, messageId, versionSeq, changeSeq`                                                                                                                                                                     |
| `ReactionsChanged`                                       | conversation                        | `orgId, conversationId, messageId, versionSeq, changeSeq, reactions` (summary; `reactedByMe` is filled by the client from its own state)                                                                                      |
| `ThreadSummaryChanged`                                   | conversation                        | `orgId, conversationId, rootMessageId, threadId, replyCount, lastReplyAt, versionSeq, changeSeq`                                                                                                                              |
| `PinChanged`, `PollChanged`                              | conversation                        | `orgId, conversationId, messageId, versionSeq, changeSeq`                                                                                                                                                                     |
| `ConversationUpdated`                                    | conversation                        | `orgId, conversationId, name?, description?, archivedAt?, memberCount, changeSeq`                                                                                                                                             |
| `SeenChanged`                                            | conversation (DM and group DM only) | `orgId, conversationId, accountId, seenSeq`                                                                                                                                                                                   |
| `UserTyping`                                             | conversation                        | `orgId, conversationId, threadId?, accountId`                                                                                                                                                                                 |
| `ChannelCreated`                                         | organization                        | Phase 1 payload; public channels only                                                                                                                                                                                         |
| `ReadStateChanged`                                       | account                             | `orgId, conversationId, lastReadSeq, version`                                                                                                                                                                                 |
| `ActivityAdded`                                          | account                             | `orgId, feedSeq, kind, conversationId, messageId`                                                                                                                                                                             |
| `MembershipChanged`                                      | account                             | `orgId, conversationId, change added                                                                                                                                                                                          | removed` |
| `PreferencesChanged`, `SavedChanged`, `ScheduledChanged` | account                             | `orgId` and the changed ids (the client refetches)                                                                                                                                                                            |
| `NotificationRequested`                                  | account                             | `orgId, notificationId, kind, conversationId, messageId, actorAccountId, preview?`                                                                                                                                            |
| `PresenceChanged`                                        | presence                            | `orgId, accountId, state online                                                                                                                                                                                               | idle     | offline, dndUntil?, customStatus?` |

| Client → server (hub)                                                                            | Check                                                                                              |
| ------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| `SubscribeConversation(orgId, conversationId)` / `Unsubscribe...` (replaces the channel methods) | may read the conversation (`IConversationReadAccess`); same `realtime.not_found` for every refusal |
| `Typing(orgId, conversationId, threadId?)`                                                       | may post; at most one per 3 s per conversation, extra calls dropped                                |
| `WatchPresence(orgId, accountIds)`                                                               | active member; replaces the connection's watch set, at most 200 ids                                |
| `ReportActivity(state active                                                                     | idle)`                                                                                             | at most one per 10 s |

**Limits.** Groups per connection rise from 200 to 600 (conversations plus presence groups). A client subscribes to every conversation in its sidebar and the open one; conversations beyond the limit are refreshed by sync when the window regains focus. **Removal** from a conversation is published after commit on `realtime:conversation-member-removed` (every node drops that account's connections from the group) and again by the consumer of `messaging.conversation-member-removed.v1`; if both broadcasts are lost the bound is the token lifetime (5 minutes), as for organization removal (open question 12). _(human review)_

## HTTP APIs

JSON, `/api/v1`, bearer token, problem details, generated OpenAPI snapshot-tested with Verify (Phase 1). Every cell route below is under `/api/v1/orgs/{orgId}` and runs the membership check first. A conversation the caller cannot read, an unknown one and one of another organization all answer `404 messaging.conversation_not_found`; a message likewise `404 messaging.message_not_found`. Page sizes are 1 to 100 (default 50) unless stated; cursors are opaque.

### Messaging

| Method and path                                                                                                                              | Permission and notes                                                                                                  |
| -------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `GET /conversations?kind=&filter=unread\|starred\|muted\|groups\|people&q=&after=&limit=`                                                    | member; the caller's conversations with last message preview, read state and prefs                                    |
| `GET /conversations/{id}`                                                                                                                    | read access                                                                                                           |
| `POST /channels { name, private, description?, memberAccountIds? }`                                                                          | `channels.create-public` or `channels.create-private`; `409 messaging.channel_name_taken`; at most 50 initial members |
| `GET /channels?after=&limit=&q=`                                                                                                             | member; browse public channels (Phase 1 route, kept)                                                                  |
| `PATCH /conversations/{id} { name?, description? }`                                                                                          | public: `channels.manage-public`; private: owner; `409 messaging.conversation_archived`                               |
| `POST /conversations/{id}/archive`, `/unarchive`                                                                                             | as above                                                                                                              |
| `POST /conversations/{id}/join`, `/leave`                                                                                                    | join: public only; leave: any member (DMs: `409 messaging.cannot_leave_dm`)                                           |
| `GET /conversations/{id}/members?after=&limit=`                                                                                              | read access                                                                                                           |
| `POST /conversations/{id}/members { accountIds }`                                                                                            | private: any member, at most 50; group DM: `409 messaging.group_dm_add_creates_new` (the client creates a new group)  |
| `DELETE /conversations/{id}/members/{accountId}`                                                                                             | private: owner (self: leave)                                                                                          |
| `PUT /conversations/{id}/owner { accountId }`                                                                                                | private: owner; target must be a member                                                                               |
| `POST /dms { accountIds }`                                                                                                                   | member; 1 to 8 others, active members; `200` existing or `201` new                                                    |
| `POST /conversations/{id}/convert-to-private-channel { name }`                                                                               | group DM member and `channels.create-private`                                                                         |
| `GET /conversations/{id}/messages?before=&after=&changed_after=&limit=`                                                                      | read access; `changed_after` as ADR 0011; Phase 1 semantics for `before`/`after`                                      |
| `POST /conversations/{id}/messages { clientMessageId, blocks, attachments?, replyToMessageId?, alsoSendToChannel? }`                         | may post (member; channels: joined, not archived); idempotent as Phase 1; rate limited                                |
| `POST /conversations/{id}/messages/{rootId}/replies { clientMessageId, blocks, attachments?, alsoSendToChannel? }`                           | may post in the parent; creates the thread on first reply; answers `{ message, threadId }`                            |
| `PATCH /messages/{id} { blocks }`                                                                                                            | author; `409 messaging.message_deleted`                                                                               |
| `DELETE /messages/{id}`                                                                                                                      | author, or `messages.remove-others-public` in public channels                                                         |
| `POST /messages/{id}/forward { targets: [{ conversationId } \| { accountIds }], note?, clientRequestId }`                                    | read access to the original, may post to each target; at most 10 targets                                              |
| `PUT` / `DELETE /messages/{id}/reactions/{emoji}`                                                                                            | read access (and may post)                                                                                            |
| `PUT` / `DELETE /messages/{id}/pin`, `GET /conversations/{id}/pins`                                                                          | `messages.pin` and may post / read access                                                                             |
| `GET /messages/{id}/seen`                                                                                                                    | author only; `{ count }` in channels, `{ accounts }` in DMs and group DMs                                             |
| `PUT /conversations/{id}/read-state { lastReadSeq, version, mode auto\|explicit }`                                                           | read access; `409 messaging.read_state_stale` with the current state                                                  |
| `POST /read-states/mark-all-read`                                                                                                            | member; explicit for every conversation, in batches of 1,000                                                          |
| `GET /sync?since=&limit=`, `GET /sync/snapshot?after=&limit=`                                                                                | member; ADR 0011; `409 messaging.resync_required`; page size up to 500                                                |
| `GET /threads?after=&limit=`, `PUT` / `DELETE /threads/{threadId}/follow`                                                                    | followed threads with unread replies                                                                                  |
| `PUT /conversations/{id}/prefs { starred?, hidden?, mutedUntil?, sectionId? }`, `GET` / `POST` / `PATCH` / `DELETE /sidebar/sections[/{id}]` | own                                                                                                                   |
| `GET /saved?tab=saved\|reminders&after=`, `PUT /saved/{messageId} { remindAt? }`, `DELETE /saved/{messageId}`                                | own; read access to the message                                                                                       |
| `GET /scheduled-messages`, `POST /scheduled-messages`, `PATCH` / `DELETE /scheduled-messages/{id}`, `POST /scheduled-messages/{id}/send-now` | own; `409 messaging.scheduled_limit`                                                                                  |
| `GET /dms/{id}/recipient-hours`                                                                                                              | member of the DM or group DM                                                                                          |
| `POST /conversations/{id}/polls { clientMessageId, question, options, multiple, anonymous, resultsVisibility, closesAt? }`                   | may post; `400 messaging.poll_invalid`                                                                                |
| `GET /polls/{messageId}`, `PUT /polls/{messageId}/vote { optionIds }`, `DELETE /polls/{messageId}/vote`, `POST /polls/{messageId}/close`     | read access; close: creator; `409 messaging.poll_closed`                                                              |
| `GET /search?...`, `GET /search/quick?q=`                                                                                                    | member; flow 7                                                                                                        |
| `GET /conversations/{id}/links?before=`                                                                                                      | read access                                                                                                           |

### Files

| Method and path                                                    | Permission and notes                                                     |
| ------------------------------------------------------------------ | ------------------------------------------------------------------------ |
| `POST /files/uploads`                                              | `files.upload` and may post in the conversation; flow 6                  |
| `POST /files/{id}/complete`                                        | uploader; `409 files.upload_incomplete`                                  |
| `GET /files/{id}/download`, `GET /files/{id}/thumbnails/{64\|360}` | access rechecked; `302` to a 60-second signed URL; `404 files.not_found` |
| `DELETE /files/{id}`                                               | uploader, or `files.remove-others-public` in public channels             |
| `GET /conversations/{id}/files?kind=&sharedBy=&before=&limit=`     | read access                                                              |

### Notifications, presence, audit, organization

| Method and path                                                                                                         | Permission and notes                                                                                                          |
| ----------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `GET /notifications?tab=all\|mentions&after=`, `POST /notifications/read-all`, `PUT /notifications/{id}/read`           | own                                                                                                                           |
| `GET` / `PUT /notification-settings`, `GET` / `PUT /conversations/{id}/notification-settings`                           | own; read access for the conversation                                                                                         |
| `GET /presence?accountIds=`                                                                                             | member; at most 200 ids; state and status only                                                                                |
| `GET /audit-log?action=&actor=&after=&limit=`                                                                           | `audit.view`                                                                                                                  |
| `POST /messages/{id}/report { reason, note? }`                                                                          | read access; `409 audit.report_exists`, `429 audit.rate_limited`                                                              |
| `GET /moderation/reports?status=&after=`, `POST /moderation/reports/{id}/resolve { action dismiss\|delete-message }`    | `moderation.review-reports`; `delete-message` also needs `messages.remove-others-public` and a public channel                 |
| `DELETE /members/{accountId}`, `POST /members/{accountId}/deactivate`, `/reactivate`                                    | `members.remove`, `members.deactivate`; `409 organization.cannot_act_on_owner`, `403 permissions.forbidden` for a higher role |
| `GET` / `POST` / `PATCH` / `DELETE /groups[/{id}]`, `PUT` / `DELETE /groups/{id}/members/{accountId}`, `GET /groups?q=` | list: member; changes: `members.manage-groups`                                                                                |
| `GET /invitations`                                                                                                      | Phase 1, plus `deliveryStatus`                                                                                                |

### Control plane

| Method and path                                                        | Purpose                                                                              |
| ---------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| `GET` / `PUT /api/v1/me/status { dndUntil?, customStatus?, timeZone }` | own account status; `400 identity.status_invalid`; fan-out as `AccountStatusChanged` |

### Cell ↔ control plane link (additive, `buf breaking` clean)

- `Welcome.epoch` (new field).
- Downstream `AccountStatusChanged { account_id, dnd_until, custom_emoji, custom_text, custom_expires_at, time_zone, version }` to every cell with a membership of the account; applied only for a newer `version`; included in `FullSync`. It is account-level profile data, not organization content, so it is allowed on the link.
- Upstream `MembershipChanged` gains the statuses `removed` and `deactivated`.

## Permissions used in Phase 2

New keys follow Phase 1's style. Owner has every permission.

| Permission key                  | Owner | Admin | Member |
| ------------------------------- | ----- | ----- | ------ |
| `channels.create-private`       | ✅    | ✅    | ✅     |
| `channels.manage-public`        | ✅    | ✅    | –      |
| `messages.mention-everyone`     | ✅    | ✅    | ✅     |
| `messages.mention-groups`       | ✅    | ✅    | ✅     |
| `messages.pin`                  | ✅    | ✅    | ✅     |
| `messages.remove-others-public` | ✅    | ✅    | –      |
| `files.upload`                  | ✅    | ✅    | ✅     |
| `files.remove-others-public`    | ✅    | ✅    | –      |
| `moderation.review-reports`     | ✅    | ✅    | –      |
| `audit.view`                    | ✅    | ✅    | –      |
| `members.remove`                | ✅    | ✅    | –      |
| `members.deactivate`            | ✅    | ✅    | –      |
| `members.manage-groups`         | ✅    | ✅    | –      |

Private channel management belongs to the channel owner, not to a role (requirements' annex). No permission grants reading others' messages. Existing organizations get the new keys on their system roles in a migration.

## Privacy rules and their tests

Each rule has at least one test in the privacy test suite (`Backroom.PrivacyTests`), run against real PostgreSQL, Valkey and RabbitMQ.

| Rule                                        | Tests                                                                                                                                                                                                                                                                    |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| No role reads others' messages              | Owner and Admin without membership get 404 on private channels, DMs, group DMs, their threads, messages, files, pins, polls, members and seen lists; no route exists that bypasses membership (architecture test over the endpoint list)                                 |
| Private names and content stay with members | For a non-member: absent from conversation lists, search (results and counts), quick search, palette, audit entries (no name), notifications, live events (group membership checked at subscribe and removed at removal), forwarded copies ("Özel kanal", no link or id) |
| Removed people lose access                  | After removal: 404 on next read, download refused, live group left on every node, `conversation_removed` in sync                                                                                                                                                         |
| Search goes through the same check          | Matching rows in unreadable conversations change neither results, counts, snippets nor timing class (no early exit)                                                                                                                                                      |
| Notifications go through the same check     | No notification for a conversation the person cannot read, including mentions of non-members and `@burada`                                                                                                                                                               |
| Anonymous polls                             | No API response, live event, log or audit entry contains vote-to-person data; rows are gone after close; voters see only their own choice                                                                                                                                |
| Files through short-lived signed URLs       | Signed URLs expire after 60 seconds; access rechecked per request; storage keys contain no names; no inline serving except thumbnails                                                                                                                                    |
| Personal data is personal                   | Sections, prefs, saved items, reminders, scheduled messages, read state and bell items of one person are unreachable for another (RLS plus account filter)                                                                                                               |
| Seen is limited                             | Only the author gets seen data; channels give a count only                                                                                                                                                                                                               |
| Presence                                    | No platform or device; time zone never returned to others; off-hours answer is a boolean and local time                                                                                                                                                                  |
| Reports copy only the reported message      | The snapshot holds that message only; private conversation names never enter it                                                                                                                                                                                          |
| No content in logs, traces or events        | Captured logs and traces of every new endpoint and consumer contain no text, names, file names, emoji or status text; event payload schemas contain ids only (architecture test)                                                                                         |
| Meeting chat (Phase 3) cannot be forwarded  | Placeholder rule: the forward source must be a conversation kind of this contract                                                                                                                                                                                        |

## Performance budgets per path

Measured as in the requirements; server times at the cell, p95 under the Phase 2 k6 load (5,000 concurrent users, 20 % active senders).

| Path                             | Budget                                                                                                              |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| Send (request to commit)         | set by ADR 0012; message visible on another client p95 300 ms same region (requirements' proposal)                  |
| Conversation or DM switch        | < 100 ms from the local cache; first page from the server p95 100 ms                                                |
| Desktop start                    | < 1.5 s with the last conversation from the cache                                                                   |
| Sync delta (up to 500 changes)   | p95 150 ms; snapshot page of 500 conversations p95 400 ms                                                           |
| Unread counts                    | one `LATERAL` query per page of conversations, capped at 100 per conversation (ADR 0011)                            |
| Search first page                | p95 500 ms (requirements' proposal); quick search p95 100 ms                                                        |
| Feed writer and notification lag | p95 1 s from commit to `ActivityAdded` / `NotificationRequested`                                                    |
| Presence                         | at most one event per person per 5 s; no fan-out to the whole organization                                          |
| Typing                           | at most one event per person per conversation per 3 s                                                               |
| Upload start and complete        | p95 100 ms each (storage transfer excluded); thumbnails p95 5 s for images up to 20 megapixels                      |
| Scheduled messages and reminders | sent within 30 s of their time                                                                                      |
| Web bundle                       | initial ≤ 170 kB JS and ≤ 30 kB CSS; the editor, search, poll and file viewers are lazy chunks ≤ 200 kB each (gzip) |
| Desktop memory outside meetings  | < 300 MB including the web view, with a full local cache                                                            |
| Long lists                       | message list, All DMs, search results, files, audit log and reports are virtualized                                 |

Hot paths with BenchmarkDotNet benchmarks: block validation and derivation, search fold, unread count query, sync delta. Fan-out of every live event goes through one group publish, never a loop over connections.

## Decisions in this contract that go beyond the requirements

1. Phase 2 includes private channels, the rich text editor, sidebar sections and All DMs (Phase 1 deferred them), and member removal, deactivation and user groups (moderation and group mentions need them).
2. Channels become conversations in place (`channels` → `conversations`, `channel_id` → `conversation_id`); message routes move to `/conversations/{id}/...` and the Phase 1 channel message routes are removed in the same PR as the web client's switch (no other clients exist).
3. Clients send rich text as our block schema; the server validates strictly, re-serializes and derives `text`, mentions and links. This refines Phase 1 decision 12 ("never trusts client-supplied blocks") from "derives blocks from text" to "validates and canonicalizes blocks".
4. Public channels have explicit membership (join and leave), which decides the sidebar and unread counts; reading and searching need only organization membership.
5. A group DM's identity is its original set of people; the same set reopens it; it is converted to a private channel in place, keeping history.
6. Threads exist only under roots in channels, DMs and group DMs; "Kanala da gönder" is a reference row in the parent, not a copy.
7. Forwarded copies show the original's current content, so edits and deletes reflect (the requirements' rule), share its attachments, and are capped at 100 per original; DM sources are labelled "Doğrudan mesaj" without a link, like private channels.
8. `read_states` gains `seen_seq` (forward only), because "mark unread" must not take back "seen"; seen data is shown only to the message's author.
9. Live events for threads go to the parent conversation's group; personal events go to a per-account group; presence goes to per-person groups watched by viewers.
10. Poll live events carry no counts; the server applies results visibility per viewer on read.
11. The search fold maps `İ`, `I`, `ı`, `i` to `i` for both index and query (better recall for Turkish, and the same for every language); typed filters are parsed by the client.
12. Manual status (do not disturb, custom status) and time zone are account-level in the control plane and reach cells over the link; automatic presence is per cell.
13. Bell entries store ids only; previews are read through the permission check, and live notification events carry at most 150 characters only to the recipient.
14. Downloads are always attachments from storage through 60-second signed URLs; image thumbnails are re-encoded WebP (SkiaSharp, MIT; ImageSharp is excluded by the license rules).
15. The cell epoch is delivered in `Welcome`, and writes are fenced until it is confirmed; restore procedures clear the confirmation.
16. Reported messages are copied into the report (the reporter's choice to share), kept 90 days after resolution.
17. Audit entries for private channels carry ids and member counts only; DMs and group DMs are not audited.
18. Off-hours in Phase 2 is the default 22:00–07:00 window in the recipient's time zone, until work hours arrive in Phase 4.
19. Limits not in the requirements: forward targets 10, mentions 50, reactions 50 distinct and 20 per person, pins 100, saved 5,000, scheduled 100, sections 50, memberships 10,000, group size 1,000, poll closing 5 minutes to 90 days, rate limits as stated.
20. New feature flags, off by default outside development until the gate passes: `Features:Conversations`, `Features:RichText`, `Features:Threads`, `Features:Files`, `Features:Search`, `Features:Notifications`, `Features:Presence`, `Features:Polls`, `Features:Scheduling`, `Features:Moderation`.

## Open questions for the owner

1. **Rich text editor** (requirements: decide before Phase 2): Lexical or TipTap. Both MIT. Recommendation: Lexical, for its smaller core (fits a lazy chunk under 200 kB with room) and because our own block schema decouples storage from either; TipTap has the larger extension ecosystem. Needed before step 2A.4.
2. **Edit history and deleted content:** confirm the requirements' proposal adopted here: no edit history; delete erases content immediately (tombstone only).
3. **Link previews:** the module plan puts them in Messaging Phase 2, the roadmap does not list them, and server-side fetching needs SSRF protection and leaks the fetch to the linked site. Proposal: Phase 5 with Integrations.
4. **Licensing and the Free plan's 5 GB quota:** the module plan puts Licensing in Phase 2, the roadmap does not. Proposal: Files records usage per organization now; quotas and plans come with Licensing in a later phase.
5. **Malware scanning of uploads** (requirements' open item): none in Phase 2, with forced attachment downloads and re-encoded thumbnails; revisit before beta?
6. **Browser notifications while no tab is open** need Web Push (a service worker and a push service, a new outbound dependency). Proposal: not in Phase 2; the desktop app covers it through the tray.
7. **Group DM size:** "at most 9 people" read as 9 including the creator. Correct?
8. **Retention:** audit log (proposal: as long as the organization exists) and report copies (proposal: 90 days after resolution).
9. **Desktop local cache:** the requirements say SQLite on desktop. Proposal: IndexedDB in the web view on both platforms for Phase 2 (one code path), SQLite only if measurements miss the start or memory budget.
10. **Anonymous polls:** may voters see their own choice (needed for "Oyumu değiştir")? And backups keep the mapping until the backup expires; acceptable, or should open anonymous votes be kept out of backups?
11. **Admin view of private channels** (existence and member count): an endpoint in Phase 2, or with the organization settings screens in Phase 5?
12. **Removal bound:** if both removal broadcasts are lost, a removed person's open connection can receive live events of that conversation until its access token expires (at most 5 minutes). Acceptable, or should the publisher filter recipients per event (more cost per send)?
13. **Read receipts:** the requirements show "seen" without an opt-out. Confirm no per-person opt-out.

## Order of implementation (small PRs; each behind its flag)

**2A Conversations core**

1. Conversations: rename `channels`, `conversation_members`, public channel join and leave, `IConversationReadAccess`, conversation groups in Realtime (replaces the channel methods), web switched to conversation routes.
2. Write ADR 0012's outcome into "Sequence numbers"; `change_seq`, `last_message_seq`, `version_seq`, tombstones, `changed_after`.
3. Cell epoch: control plane field, `Welcome`, connector storage, write fencing, cursors with epoch. _(human review)_
4. Rich text: block schema, validation, derivation, mentions and links tables; web editor (after open question 1).
5. Private channels: create, members, owner rules, archive; privacy tests. _(human review)_
6. DMs and group DMs, conversion to private channel; privacy tests. _(human review)_
7. Edit and delete (tombstones), reply with quote.
8. Reactions and pins; typing.
9. Read state and seen, unread counts, mark read/unread, account group events. _(human review)_
10. Threads and followers, "also send to channel".
11. Activity feed writer, `feed_pending`, retention job, sync delta and snapshot; web local cache and sync. _(human review)_
12. Forwarding. _(human review)_
13. Sidebar sections, prefs, All DMs; web screens.
14. Saved messages and reminders; scheduled messages and the off-hours warning.
15. Polls. _(human review for anonymous polls)_

**2B Files, search, palette**

16. Files module: uploads, completion, signed downloads, deletion, retention jobs; attachments in Messaging. _(human review)_
17. Thumbnails in the worker; Files tab and Links tab; long text as `.txt`.
18. Search: messages and channels (fold, GIN, permission in query), files and people providers; web search with typed filters. _(human review)_
19. Quick search and the command palette.

**2C Presence and notifications**

20. Account status in the control plane, `AccountStatusChanged`, Presence module and hub presence; status menu.
21. Notifications: preferences, decision order, bell, `NotificationRequested`; web and desktop notifications and tray (new Tauri plugin checked against the 7-day rule).

**2D Moderation and audit**

22. Member removal, deactivation, reactivation; `MembershipChanged` statuses. _(human review)_
23. User groups and group mentions.
24. Audit module and log view; invitation delivery status.
25. Reports and review.

**Gate**

26. End-to-end scenarios of the gate on two cells; Phase 2 k6 test; measurements in the PR.
