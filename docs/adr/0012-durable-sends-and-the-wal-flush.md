# 0012. Durable sends: keep the synchronous commit and make the WAL flush a storage requirement

- Status: Accepted
- Date: 2026-11-25

## Context

The first load test (`load-tests/results/2026-11-24-desktop.md`) missed two thresholds: `send_ms` p95 was 355 ms and `message_visible_ms` p95 322 ms, with medians near 9 ms. The cause was PostgreSQL's commit waiting for the WAL flush on Docker Desktop's virtual disk. With `synchronous_commit = off` the p95 was 8 ms, but that setting can lose acknowledged messages.

The owner's requirement is both: **no acknowledged message is ever lost, and sends are fast.** The guarantees of ADR 0011 and decision 12 of the Phase 1 contract stay:

- `seq` follows commit order per conversation, so `after={seq}` never skips a message;
- a send is idempotent by `clientMessageId`;
- the outbox row is written in the same transaction as the message;
- a live event goes out only after the message is durable;
- a sequence number that anyone has seen is never given to another message.

The suspicion to check was that our design amplifies the disk's stalls: each send runs `UPDATE messaging.channels SET last_seq = last_seq + 1 … RETURNING` and holds the channel row lock until its commit, WAL flush included, so one stalled commit queues every send to that channel.

This ADR records what the spike measured, the options it weighed, and a prototype of the strongest alternative. All numbers are in `load-tests/results/2026-11-24-send-durability.md`. Only the first ten k6 runs there were taken on a quiet machine; the statements below rest on those and on the crash checks.

## What the measurements show

1. **The disk stalls by the clock, not per commit.** A probe that commits a few bytes synchronously, and nothing else, saw 10, 11 and 11 stalls per minute at 5, 20 and 100 commits per second. Each stall blocks every WAL flush for 0.3 to 0.7 s. `wal_sync_method` makes no difference (`fdatasync`, `fsync`, `open_datasync`, `open_sync` all stall 8 or 9 times in 45 s). During the k6 runs, 4 to 10 % of the probe's commits waited more than 50 ms for the disk.
2. **The share of slow sends is the share of time the disk is stalled.** 6 to 8 % of sends took more than 50 ms in every profile with the current design: 10 sends/s over 8 channels, 10 sends/s to one channel, 50 sends/s over 8 channels and 50 sends/s to one channel. Sending everything to one channel, at five times the rate, did not raise that share. **The channel row lock did not amplify the stalls** at these rates: while a flush is stalled, a send to any channel waits for it, with or without the lock, because PostgreSQL flushes one WAL for the whole server.
3. **Reads never stall** (history p95 5 ms), because they do not wait for a flush.
4. **Turning the flush wait off is fast and loses data.** With `synchronous_commit = off`, p95 was 7 ms. In the crash check (`load-tests/tools/crash-check.mjs`: three crashes while sending 200 messages/s) 54 of 3,903 acknowledged messages were gone afterwards, and all 54 sequence numbers had been given to other messages. The current design lost 0 of 3,857.

The conclusion that drives the decision: **a durable acknowledgment cannot be faster than one WAL flush on local storage.** "Durable" here means the commit record is on the cell's disk, and only the flush puts it there. When the disk cannot flush for half a second, every lossless design that relies on that disk waits that half second. The one alternative is a different meaning of durable: acknowledging once a quorum of nodes holds the record in memory, so that no single crash loses it. PostgreSQL does not offer that (every level of `synchronous_commit` above `off` waits for the local flush first), and it would mean another storage system for messages; it belongs to the replication question left open below, not to the send path. A design can avoid adding to that wait; it cannot remove it. The row lock adds almost nothing, so there is almost nothing for a redesign to win on this disk.

## Options considered

PostgreSQL facts the analysis rests on:

- A transaction with `synchronous_commit` other than `off` flushes the WAL up to its commit record (and waits for synchronous standbys, if configured) **before** other sessions can see its effects.
- A transaction with `synchronous_commit = off` is visible at once and durable later, when the WAL writer flushes (within about three times `wal_writer_delay`, 200 ms by default).
- Crash recovery replays a prefix of the WAL. A transaction survives exactly when its commit record was flushed, and then everything written to the WAL before it survives too. The same holds for a replica: it has a prefix of the primary's WAL.

### (a) Group commit settings (`commit_delay`, `commit_siblings`) with the current design

`commit_delay` makes a committing session wait a little so that other sessions' commits share its flush. Sends to one channel never commit at the same time (the row lock), and sends to different channels already share a flush whenever one is in progress. Measured with `commit_delay = 2 ms`, `commit_siblings = 1`: no gain (7.2 % and 7.1 % of sends over 50 ms, against 7.7 % and 7.1 % without). Crash, failover and replica behavior are unchanged, because only timing changes. **Rejected: no effect.**

### (b) Commit without waiting for the flush, then wait for the flush before acknowledging

The send transaction sets `synchronous_commit = off` for itself, so its commit releases the row lock at once; the request then waits until the WAL is flushed past its commit before it answers 201 and publishes the live event.

Done naively this is **unsound**. After a crash the unflushed transaction is gone; it was never acknowledged, and the client's retry stores it again. But between the commit and the flush the row is visible to everyone else:

| Hole | What goes wrong |
| --- | --- |
| Readers | `GET …/messages` returns the row and the client's cursor moves to its `seq`. After a crash the row and the counter increment are gone (they are in the same lost transaction), the next send gets the same `seq`, and that client's `after={seq}` never returns it: a skipped message. |
| Repeats | A retry with the same `clientMessageId` finds the row and answers 200. That is an acknowledgment of a message that is not durable. |
| Sequence reuse | Follows from the first hole: a number that a reader has seen is given to another message. |
| Outbox relay | The relay sees the outbox row and publishes `messaging.message-posted.v1` for a message that a crash then removes. |

Mitigations considered:

- **Bump the cell epoch on crash recovery** (ADR 0011 has the epoch for restores and failovers). A crash can be detected inside the database (a marker row in an unlogged table disappears on crash recovery and on a promoted replica). But every client would do a full resync after every database crash, readers would still have shown messages that then vanish, and the relay hole stays. Rejected.
- **Readers wait for the flush themselves** (compare `pg_current_wal_flush_lsn()` with the insert position after reading). The WAL is shared by the whole cell, so a read would wait whenever anyone's commit is unflushed, and reads would inherit the disk's stalls. Rejected: reads are the one path that is fast today.
- **Hide unflushed rows behind a pending marker.** This is the variant the spike prototyped (below). It closes all four holes. The prototype was measured and then dropped; its code is not in the repository, and this section is its record.

#### The prototype: acknowledge after the flush (`AcknowledgeAfterFlush`)

- **First transaction (no flush wait).** As today (row lock, `seq`, message, outbox row), plus a row in `messaging.pending_messages (org_id, channel_id, seq, created_at)`. The marker commits with the message, so nobody ever sees the message without it.
- **Second transaction (synchronous).** Deletes the marker. When that commit is visible it has been flushed, and the flush covers the first transaction, whose commit is earlier in the WAL. The absence of a marker is therefore proof that the message is durable. Anyone may delete any marker they can see, as long as they do it in a synchronously committed transaction, so the proof does not depend on who cleans up.
- **Acknowledgment and live event** wait for the second commit. If it fails, the request fails, nothing is announced, and the client repeats the send.
- **Readers** list only messages below the channel's lowest marker (one extra indexed subquery in the same statement, so the same snapshot). A list is a gap-free prefix of durable messages, and a cursor taken from it can never pass a pending message.
- **Repeats** check the marker and wait for the confirmation before answering 200.
- **Abandoned markers.** A sender that dies between its two transactions leaves a marker that would hide the channel's newer messages. A later send, or a read, of that channel removes markers older than 5 s. A read does it in its own transaction, which commits synchronously before the page is answered.
- **Outbox relay.** A relay option makes the relay flush the WAL (one small synchronous commit on a second connection) after reading a batch and before publishing it.
- **Sequence reuse.** A crash can still take back an unflushed transaction and its counter increment, and the number is then given to another message. That is harmless now: nobody was shown the number (not the sender, not a reader, not a listener, not a consumer).

Failure analysis of the prototype:

| Event | Outcome |
| --- | --- |
| Database crash before the first commit is flushed | Message, marker, outbox row and counter increment are lost together. Nobody saw any of it. The client's retry stores the message again. |
| Database crash after the first commit is flushed, before the second | The message is durable but hidden. The client's retry confirms it (200); without a retry, the next read or send of the channel after 5 s reveals it. No live event is sent for it, as today when a node dies between commit and publish; clients get it by catch-up. |
| App node dies between the two transactions | Same as the previous row. Until the marker is removed, reads of that channel end below it, while live events of newer messages still arrive. |
| Confirmation times out or its connection drops | The request fails with 5xx and announces nothing. The retry finds the row and confirms it. |
| Failover to an asynchronous replica | The replica has a prefix of the WAL: either the pair (message and marker) or neither, and a marker's deletion only after the message. Acknowledged messages can be missing on the replica, exactly as with the current design; that is what the cell epoch of ADR 0011 is for. |
| Synchronous standbys | The second commit waits for them as any synchronous commit does, so the acknowledgment keeps whatever level the server is configured for. |
| Reads on a replica | Markers and messages replicate in WAL order, so the same rule holds there. |
| `synchronous_commit = off` as a server, database or role default | Would silently break the proof. The confirming transactions switch it on for themselves; a deployment check should still refuse to start in this mode with such a default. |

The prototype's nine integration tests held the confirmation at the database with a trigger and an advisory lock, and killed its backend where the scenario needed it. They covered: no answer, no live event and no listing before the confirmation; a list that ends below a pending message and later returns it; a repeat that waits; a killed confirmation followed by a successful retry; a simulated loss of the unflushed transaction, after which the reused number is returned to the reader whose cursor stayed below it; abandoned and recent markers; and markers of another organization (row-level security). The crash check on the isolated stack lost 0 acknowledged messages in this mode as well (see the results).

**Measured result: no gain on this disk, and a cost.** In the clean runs the share of slow sends stayed the share of stalled disk time (5.3 % and 8.4 % of sends over 50 ms, with the probe at 4.4 % and 10.1 %), and the median rose from about 8.5 ms to about 12 ms because of the second transaction. Later runs, taken while the machine was busy with other work, do not change this: with both paths in one stack the prototype was not ahead, and both paths had runs that collapsed (see the results).

What the prototype would cost if adopted:

- **A rule for every reader of messages, for good.** Unread counts, `changed_after`, thread summaries, search and notifications of Phase 2 must all apply "below the lowest pending marker". A query that forgets it shows messages that a crash can remove, and nothing fails until that crash.
- Two transactions and more WAL per send; a second connection for the relay; a 5 s window in which a dead node hides the tail of a channel from reads.
- Live events can arrive for `seq` n+1 a few milliseconds before n is confirmed. The web client already advances its catch-up cursor only from server reads, which is the rule every client would have to keep.
- Its one expected benefit is throughput in a single conversation: confirmations of one channel share a flush, so one channel is no longer limited to one send per commit. In the clean run the current design showed no queueing at 50 sends/s to one channel, so nothing needs that today. The measurements could not confirm the benefit either: the later runs are too noisy.

**Rejected for now.** It is sound, but it buys nothing where the problem is, and it adds a permanent visibility rule to a privacy-critical read path.

### (c) Other designs

- **Allocate `seq` outside the row lock, with a visibility watermark.** The counter is advanced in a short transaction of its own (or under an advisory lock taken only for the assignment), and message transactions of one channel then commit in parallel. Commit order no longer follows `seq`, so readers again need a watermark below the lowest in-flight number, and in-flight numbers must be visible before their transactions commit, which means marker rows, their cleanup and the same reader rule as in (b). The acknowledgment still waits for a flush. Rejected: the machinery of (b) with a weaker ordering argument.
- **A database sequence instead of the counter row** (per cell or per organization). It never blocks and never rolls back. Rejected on privacy: the gaps in a channel's numbers would show every member how many messages were sent elsewhere, including in private channels and other organizations.
- **One writer per channel in the application, with a group flush.** Sends to one channel are batched into one transaction. The semantics stay exactly as today, so it is sound by construction. But it works per node only (several nodes still meet at the row lock), it needs a write path outside the request's unit of work (organization context, row-level security, isolating one bad message in a batch), and it only helps when one channel receives more sends than one per flush. Rejected for now; it is the design to revisit for a broadcast-scale conversation, which ADR 0011 already sets aside as a feature with its own design.
- **`synchronous_commit = off`** for the send. Rejected: it loses acknowledged messages (measured above).
- **Announcing before durability** (a tentative live event that the client later confirms). Rejected: it breaks the rule that nothing is announced that is not stored.

## Decision

1. **Keep the send path of ADR 0011 and contract decision 12 unchanged:** one transaction, synchronous commit, the channel row lock held until the commit. It is lossless (crash check: 0 lost), and it is as fast as a lossless design can be on a given disk.
2. **Deployment requirement: WAL flush p99 at most 10 ms on cell storage.** It is measured on the device that holds the cell database's WAL, under the 5,000-user load. It is checked with the flush probe (`load-tests/tools/wal-flush-probe.sql`, described in `load-tests/README.md`) before a cell's storage is accepted, and watched in production (`track_wal_io_timing = on`, WAL sync time from `pg_stat_io`). This goes into the deployment checklist of the deploy phase.
3. **Read load-test results together with the probe.** Every `messaging` run records the probe beside it. A run whose probe has more than about 1 % of its commits over 50 ms measures the disk, not the product, and gives no budget verdict for sends. Docker Desktop's virtual disk is such a disk; budget numbers need a stack on real storage, as the first run's report already said.
4. **Keep the crash check** as the test to run before any future change to how sends become durable.
5. **Do not adopt the prototype, and do not keep its code.** The description above is what remains of it, as the worked-out starting point if a single conversation ever needs more throughput than one send per commit.
6. **Close the open question on a quiet machine** before Phase 2 adds reactions and edits, which take numbers from the same per-conversation counter: one channel at 50 changes/s and more, both paths alternating, with CPU and connection-pool waits recorded (results, "Later runs"). If the current design does queue there, the remedies in order are a bound on sends waiting per channel on a node (answer 429 with `Retry-After` instead of holding pooled connections), then the prototype or the single-writer batch for that conversation.

Without the wait for the disk the unchanged code path measured `send_ms` p95 7 ms and p99 8 ms (the `synchronous_commit = off` reference run). A disk that meets the target adds its flush time to that, nothing more.

## Consequences

- **ADR 0011:** unchanged. Its cost note ("about one change per commit time" per conversation) holds: in the clean run 50 sends/s to one channel ran without queueing. Its epoch remains the answer to losing acknowledged commits in an asynchronous failover.
- **Contract decision 12:** unchanged.
- **End-to-end and privacy tests:** unchanged.
- **Performance budget:** "message visible on another client, p95 300 ms" and the `send_ms` threshold are met only on storage that meets the flush requirement. On Docker Desktop they will keep failing now and then, by design of the disk.
- **Failover.** "No acknowledged message is lost" holds for a crash of the database on intact storage. Across a failover to an asynchronous replica it does not; see the open questions.
- **Connection pool.** While a flush is stalled, every waiting send holds a pooled connection (30 by default per app node). By arithmetic, 50 sends/s and a 0.6 s stall fill the pool, and reads then queue behind the sends; this was not measured in isolation. `Database:MaxPoolSize` must be sized for the send rate times the longest tolerated flush, which belongs to the k6 run on proper hardware.
- **Load tests** gain the probe, the crash check, `SEND_TARGET=one`, and a fix in the k6 hub client, which counted one message per WebSocket frame although the server can put several into one.

## Open questions

These are not decided here.

1. **Synchronous replication for failover** (deploy-phase ADR). "No loss" across a database failover needs synchronous replication, which adds a network round trip and the standby's flush to every send. Without it, a failover can lose acknowledged commits, and the cell epoch and resync of ADR 0011 are the answer. The deploy-phase ADR chooses.
2. **PostgreSQL's data in memory for the isolated load-test stack.** It would make runs on a developer machine measure the code path instead of the virtual disk. An option, not adopted; the development stack would not change either way.
3. **One very busy channel** (decision 6): the rerun on a quiet machine.
