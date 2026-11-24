# Send durability and latency: measurements for ADR 0012, 2026-11-24

Measurements behind `docs/adr/0012-durable-sends-and-the-wal-flush.md`: where the slow sends of the first run (`2026-11-24-desktop.md`) come from, and what three ways of committing a send do about them.

**Read this first.** Only the runs of roughly the first 45 minutes are usable for conclusions ("Clean runs"). Later the same machine was busy with other work (other builds and test suites with their own containers; 60 to 67 % of the CPU was in use when checked, and read latency was far higher). Those runs are listed for completeness under "Later runs" and prove nothing either way.

## Setup

| | |
| --- | --- |
| Machine | The desktop of the first run: 8 cores, 16 threads, 31 GB, Windows 11 Pro, Docker Desktop (WSL 2). Another AppHost ran on it the whole time (idle). |
| Stack | The AppHost in an isolated run (random ports, no volumes), hosts in Release builds, started by a small runner with Aspire testing. A fresh stack, and so a fresh PostgreSQL 18.3 container, for each send path. |
| Fixture | `ORGS=4 MEMBERS=10 CHANNELS=2 SEED_MESSAGES=120`: 40 users, 8 channels. All organizations on one cell. |
| Load | `messaging` scenario, 120 s, `LISTENERS=40 READ_RATE=10`, and `SEND_RATE` 10 or 50. "Spread" sends to random channels; "one channel" is `SEND_TARGET=one`. |
| Flush probe | `tools/wal-flush-probe.sql` with `pgbench -R 5` inside the PostgreSQL container during every run: one tiny synchronous commit five times a second. "Probe over 50 ms" is the share of those commits that took more than 50 ms, waiting behind an earlier one included. It estimates the share of the run during which a commit had to wait that long for the disk. |
| Slow sends | "Sends over 50 ms" is the share of `send_ms` samples above 50 ms, computed from k6's JSON output. Percentiles alone hide whether a tail is 3 % or 8 % of the sends. |

Send paths:

- **Current**: the send path of ADR 0011 and contract decision 12.
- **Group commit**: the current path with `commit_delay = 2000` (2 ms) and `commit_siblings = 1` set on the isolated server.
- **Prototype**: the acknowledge-after-flush path that ADR 0012 describes and rejects. Its code lived on the spike branch only and is not in the repository.
- **No flush wait**: the current path with `synchronous_commit = off` on the isolated server. Not lossless; a reference for what the code costs without the disk.

## The disk

The probe alone, 60 s at three rates, counting stall episodes (slow commits more than a second apart are separate episodes):

| Probe rate | Commits | Over 50 ms | Stall episodes |
| --- | --- | --- | --- |
| 5/s | 289 | 6.9 % | 10 |
| 20/s | 1,165 | 10.0 % | 11 |
| 100/s | 6,017 | 10.8 % | 11 |

Twenty times more commits, the same number of stalls: the disk stalls by the clock (about every 5 to 8 s, for 0.3 to 0.7 s), not per flush. Fewer flushes would not mean fewer stalls.

`wal_sync_method`, 45 s each at 20 commits/s: `open_datasync` 9 episodes, `fsync` 8, `open_sync` 8 (`fdatasync`, the default, as above). No setting avoids them. The cause is below PostgreSQL (the data disk of Docker Desktop's virtual machine, which all containers share) and was not investigated further.

## Clean runs

| Path | Target | Sends/s | `send_ms` p50 | p95 | p99 | max | Sends over 50 ms | Probe over 50 ms | `message_visible_ms` p50 | p95 | p99 | History p95 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Current | spread | 10.0 | 8.6 | 93 | 443 | 694 | 7.7 % | 4.0 % | 9 | 92 | 443 | 15 |
| Current | one channel | 10.0 | 8.4 | 157 | 517 | 705 | 7.1 % | 5.3 % | 8 | 159 | 517 | 5.3 |
| Current | spread | 49.9 | 7.6 | 115 | 462 | 685 | 6.2 % | 7.1 % | 8 | 60 * | 431 | 5.0 |
| Current | one channel | 50.0 | 7.5 | 97 | 459 | 737 | 6.1 % | 5.1 % | 8 | 97 | 460 | 5.2 |
| Group commit | spread | 10.0 | 8.2 | 164 | 510 | 655 | 7.2 % | 7.7 % | 9 | 131 * | 501 | 5.1 |
| Group commit | one channel | 10.0 | 8.1 | 159 | 416 | 613 | 7.1 % | 7.8 % | 8 | 159 | 416 | 5.1 |
| Prototype | spread | 10.0 | 11.7 | 68 | 362 | 578 | 5.3 % | 4.4 % | 12 | 48 * | 362 | 6.2 |
| Prototype | one channel | 10.0 | 11.7 | 236 | 507 | 644 | 8.4 % | 10.1 % | 12 | 21 * | 467 | 6.6 |
| No flush wait | spread | 10.0 | 6.0 | 7.0 | 8.0 | 52 | 0.1 % | n/a | 6 | 8 | 9 | 5.4 |
| No flush wait | one channel | 10.0 | 5.7 | 6.8 | 7.9 | 10 | 0.0 % | n/a | 6 | 7 | 8 | 5.3 |

Milliseconds. No send failed in any run. Throughput always followed the arrival rate.

\* `message_visible_ms` is too low in the tail in these rows, and may be slightly low in the others. Until it was fixed during this work, the k6 hub client counted one message per WebSocket frame, and the server puts several messages into one frame when they are published together, which is what happens after a stall. `send_ms` is not affected; use it to compare. A live event is published right after the send is acknowledged in every path, so the two measures move together.

What they show:

1. **Sends over 50 ms track the probe.** In every row with a flush wait, the share of slow sends is close to the share of time the probe found the disk making a commit wait (5 to 8 % against 4 to 10 %). p95 lands inside or just outside that tail depending on whether the stalled share of the run is above or below 5 %, which is why p95 jumps between 68 and 236 ms while the medians do not move.
2. **One channel is no worse than eight**, at 10 and at 50 sends/s. The channel row lock does not amplify the stalls at these rates.
3. **Group commit settings change nothing.**
4. **The prototype has the same tail** and a median about 3 ms higher (the second transaction).
5. **Without the flush wait the code path takes 6 to 8 ms** at every percentile. That is what a disk whose flush is fast would leave.
6. **Reads are unaffected** in every path (p95 about 5 ms).

## Crash check

`tools/crash-check.mjs`: 200 sends/s to one channel for 24 s (4,800 sends) while one PostgreSQL server process is killed three times (`kill -9` of the WAL writer: the postmaster then restarts every backend through crash recovery and loses what was not flushed). Afterwards the channel is read back through the API.

| Path | Acknowledged | Failed sends | Acknowledged but missing | Acknowledged `seq` now another message |
| --- | --- | --- | --- | --- |
| Current | 3,857 | 943 | **0** | **0** |
| No flush wait | 3,903 | 897 | **54** | **54** |
| Prototype, run 1 | 159 | 4,641 | **0** | **0** |
| Prototype, run 2 | 3,242 | 1,558 (all `500`) | **0** | **0** |

- `synchronous_commit = off` loses acknowledged messages, and their sequence numbers are reused: a client that saw one of them would skip its replacement. The check finds it, so a zero in the other rows means something.
- Neither lossless path lost anything.
- The prototype failed more sends around a crash, probably because each send uses two connections in a row and both break. Run 1 acknowledged almost nothing; the same load without a crash was acknowledged in full (2,400 of 2,400 in 12 s). Why run 1 went so badly was not investigated. The prototype's runs fell in the busy period described below.

## Later runs (machine busy with other work; not usable)

After the first 45 minutes or so, read latency rose (history p95 from 5 ms to between 14 and 880 ms) and send medians rose in both paths (current: 8.5 to between 10 and 16 ms), although the probe did not show more stalls. Other sessions were building and running container-based tests on the same machine. These rows show how far the numbers can move, not which path is better:

| Path | Target | Sends/s | `send_ms` p50 | p95 | p99 | max | Sends over 50 ms | Probe over 50 ms | History p95 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Prototype | spread | 49.9 | 12.3 | 102 | 421 | 760 | 8.7 % | 4.9 % | 20 |
| Prototype | one channel | 49.5 | 12.2 | 558 | 1,240 | 2,828 | 27.8 % | 7.3 % | 79 |
| Prototype | one channel | 50.0 | 12.0 | 147 | 340 | 1,209 | 15.2 % | 2.7 % | 42 |
| Prototype | one channel | 50.0 | 11.5 | 110 | 379 | 796 | 7.7 % | 2.1 % | 14 |
| Prototype | one channel | 10.0 | 13.4 | 137 | 350 | 612 | 19.5 % | 4.3 % | 42 |
| Prototype | spread | 9.9 | 14.9 | 414 | 781 | 1,119 | 20.8 % | 7.9 % | 153 |
| Current | one channel | 48.5 | 13.8 | 1,478 | 3,753 | 8,183 | 26.7 % | 6.5 % | 360 |
| Current | spread | 10.0 | 15.6 | 213 | 425 | 637 | 18.4 % | 9.5 % | 42 |
| Current | one channel | 10.0 | 12.7 | 137 | 313 | 557 | 14.3 % | 4.5 % | 50 |

To take the changing conditions out, both paths then ran in one stack, chosen per organization by a temporary, uncommitted switch, so that they shared the disk and the machine at every moment:

| Run | Path | Sends/s | `send_ms` p50 | p95 | p99 | Sends over 50 ms | Probe over 50 ms |
| --- | --- | --- | --- | --- | --- | --- | --- |
| One channel each, together | Current | 9.7 | 11.2 | 73 | 162 | 6.9 % | 3.2 % |
| | Prototype | 10.3 | 14.8 | 107 | 207 | 10.1 % | |
| One channel each, together | Current | 47.0 | 9.9 | 2,274 | 4,048 | 22.7 % | 5.4 % |
| | Prototype | 47.4 | 13.5 | 2,536 | 5,446 | 16.7 % | |
| One channel each, together (repeat) | Current | 48.1 | 12.5 | 1,791 | 3,005 | 31.5 % | 3.7 % |
| | Prototype | 48.5 | 15.7 | 2,061 | 3,805 | 30.1 % | |
| Spread, together | Current | 9.8 | 16.8 | 1,063 | 2,041 | 22.2 % | 1.8 % |
| | Prototype | 19.9 | 21.7 | 1,977 | 3,073 | 28.2 % | |
| One channel, alternating, 60 s each | Current | 48.4 | 95.8 | 2,202 | 2,871 | 55.2 % | 2.8 % |
| | Prototype | 49.5 | 16.0 | 408 | 1,428 | 14.9 % | 1.4 % |
| | Current | 50.0 | 9.8 | 23 | 116 | 2.7 % | 1.6 % |
| | Prototype | 48.9 | 16.7 | 1,661 | 2,592 | 26.9 % | 3.8 % |
| | Current | 50.0 | 10.0 | 225 | 579 | 13.1 % | 8.2 % |
| | Prototype | 50.0 | 12.8 | 75 | 269 | 7.8 % | 4.4 % |

In these runs the two paths are slow together and neither is consistently ahead: each has one run in which it collapses and one in which it is fine, with the disk nearly idle according to the probe. Something other than the commit path limited the stack at that time. Candidates, none verified: CPU taken by the other work (including from the load generator), the application's connection pool (30 connections per node; every send waiting for a commit holds one, so at 50 to 100 sends/s a short stall fills it and reads queue behind it), and the per-channel row lock when a commit takes 10 ms instead of 3 (a channel then handles at most about 100 sends/s, and a backlog drains slowly).

**Open question for a quiet machine:** rerun "one channel, 50 sends/s" for both paths, alternating, at least five times each, with CPU and pool waits recorded. Until then the claim "the row lock is harmless at 50 sends/s into one channel" rests on the two clean runs above.

## How to repeat

```sh
# Isolated stack up, fixture provisioned, token server running (README).
k6 run -e LISTENERS=40 -e SEND_RATE=10 -e READ_RATE=10 -e DURATION=120s -e SEND_TARGET=one \
  --out json=points.json load-tests/scenarios/messaging.js
# Beside it, in the PostgreSQL container: the flush probe (README, "WAL flush probe").
# Group commit and no flush wait: ALTER SYSTEM SET ... on the isolated server only, then SELECT pg_reload_conf(); reset afterwards.
```

The tool's endpoints have no defaults and it refuses the development stack's ports (README, "Safety"). That guard was added after this work provisioned a fixture on the development stack by mistake.
