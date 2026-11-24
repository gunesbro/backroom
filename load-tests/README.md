# Load tests

[k6](https://k6.io) scenarios for the cell API and the live hub, plus a small Node tool that prepares users and tokens.

- k6 is a separate binary (v1.0 or newer, because the scenarios use `k6/websockets`), licensed AGPL-3.0. That is acceptable here: it is a test tool run as its own process, nothing of it is shipped or linked (CLAUDE.md, "Libraries"). It is not installed by the repository; install it yourself (<https://grafana.com/docs/k6/latest/set-up/install-k6/>).
- The tool needs Node 20 or newer and has no dependencies.

## Safety

- **Never run against production.** The tool creates users and organizations, and the scenarios send thousands of messages. It refuses targets other than `localhost` unless `LOADTEST_ALLOW_REMOTE=yes`; use that only for a dedicated load-test stack.
- **Do not load the AppHost you work in.** Start a separate stack (a second checkout or worktree has its own volumes) and point the tool at it.
- Outside production all email goes to Mailpit, and test users use `lt-<run>-<org>-<member>@loadtest.test` addresses. The setup tool sends invitations (membership exists only through them), at most 49 per organization; the scenarios themselves **never create invitations, organizations or users**.
- Nothing secret is committed: the fixture (`.data/`, git-ignored) holds throwaway passwords of throwaway users, and tokens only live in the memory of the token server.

## How it works

1. `provision` creates the users in Keycloak through the admin API (verified emails, `.test` addresses), then signs them in through the **authorization code flow with PKCE** (client `desktop`, loopback redirect; no direct grant exists or is enabled), creates the organizations, channels, seed messages and memberships, and writes `.data/fixture.json`.
2. `serve` signs every user in again and keeps one refresh chain per user (refresh tokens are single use; one refresh at a time per user, as in the contract). It serves the current **cell token** of a user on `http://127.0.0.1:5999/token/{index}`. Access tokens live 5 minutes and are renewed every 150 s, so runs are not limited to 5 minutes and no token is written to disk.
3. k6 reads the fixture, asks the token server for a user's token (cached per VU until 45 s before expiry) and calls the cell.

```powershell
# Terminal 1: a load-test stack is running (see the AppHost; Keycloak admin credentials are the AppHost's parameters).
# Keycloak's development certificate must be trusted by Node: NODE_EXTRA_CA_CERTS=<path to the dev CA>.
$env:KEYCLOAK_ADMIN_USER = "admin"; $env:KEYCLOAK_ADMIN_PASSWORD = "<from the stack>"
node load-tests/tools/loadtest.mjs provision      # ORGS=2 MEMBERS=5 CHANNELS=2 by default
node load-tests/tools/loadtest.mjs serve          # leave running

# Terminal 2
k6 run load-tests/scenarios/messaging.js
k6 run load-tests/scenarios/catch-up.js
k6 inspect load-tests/scenarios/messaging.js      # syntax and options check, no load

# Afterwards
node load-tests/tools/loadtest.mjs cleanup        # deletes the Keycloak users; organizations stay in the cells
```

## Scenarios

### `messaging`

| Part | What it does | Metric |
| --- | --- | --- |
| `senders` | `SEND_RATE` messages per second (arrival rate) to random public channels, 1 to 4000 characters | `send_ms` |
| `readers` | `READ_RATE` per second: first page (`limit` 50, newest first), then `HISTORY_PAGES` older pages by keyset (`before={next}`) | `history_first_page_ms`, `history_next_page_ms` |
| `listeners` | `LISTENERS` SignalR connections (MessagePack, WebSockets) subscribed to every channel of their organization | `ws_ready_ms`, `message_visible_ms` |

`message_visible_ms` is the time from the sender stamping the message ("lt:<epoch ms>:" at the start of its text) to a listener receiving the `MessagePosted` frame. The hub speaks MessagePack only; `lib/signalr.js` encodes the handshake, `SubscribeChannel` and pings itself and does not decode the frames, it only finds the stamp in the bytes. Gaps: sender and listener must share a clock (one load generator machine, or synchronized clocks), and the measure includes the network between the generator and the stack, so run the generator near the stack for budget numbers.

### `catch-up`

A reconnect storm (`ramping-arrival-rate`: ramp to `STORM_RATE` clients per second in 10 s, hold `STORM_PEAK`, ramp down). Each client reads the newest `seq` (`limit=1`), pretends its last seen sequence is `CATCHUP_LAG` (default 100) older, pages forward with `after=` (oldest first, `limit` up to 100) until `next` is null, and reconnects the hub (`CATCHUP_HUB=false` skips the hub). Metrics: `catchup_page_ms`, `catchup_total_ms`, `ws_ready_ms`. Seeded channels hold `SEED_MESSAGES` (default 120) messages, so a lag of up to 100 finds data.

## Thresholds against the budget

A failed threshold makes k6 exit non-zero. Defaults (override with the variables in the second column):

| Threshold | Default | Variable | Budget (CLAUDE.md) |
| --- | --- | --- | --- |
| `send_ms` p95 | 250 ms | `THRESHOLD_SEND_P95_MS` | engineering target, inside the 300 ms visible budget |
| `message_visible_ms` p95 | 300 ms | `THRESHOLD_VISIBLE_P95_MS` | message visible on another client p95 300 ms (proposal) |
| `history_first_page_ms` p95 | 150 ms | `THRESHOLD_FIRST_PAGE_P95_MS` | server share of "channel switch under 100 ms" (cache first on the client) |
| `history_next_page_ms` p95 | 150 ms | `THRESHOLD_NEXT_PAGE_P95_MS` | same |
| `catchup_total_ms` p95 | 500 ms | `THRESHOLD_CATCHUP_P95_MS` | engineering target for a 100-message catch-up |
| `ws_ready_ms` p95 | 1000 ms | `THRESHOLD_WS_READY_P95_MS` | engineering target (connect, handshake, subscribe) |
| failed sends, failed history requests | 0 sends, under 1 % requests | | |

There is no search in Phase 1, so the search p95 500 ms row has no scenario yet.

## Configuration

| Variable | Default | Used by |
| --- | --- | --- |
| `KEYCLOAK_URL`, `KEYCLOAK_REALM`, `CONTROL_PLANE_URL`, `MAILPIT_URL` | `https://localhost:8443`, `backroom`, `http://localhost:5100`, `http://localhost:8025` | tool |
| `KEYCLOAK_ADMIN_USER`, `KEYCLOAK_ADMIN_PASSWORD` | required | `provision`, `cleanup` |
| `ORGS`, `MEMBERS`, `CHANNELS`, `SEED_MESSAGES`, `REGION`, `CONCURRENCY` | 2, 5, 2, 120, `eu-dev`, 4 | `provision` |
| `TOKEN_SERVER_PORT`, `REFRESH_SECONDS`, `FIXTURE` | 5999, 150, `.data/fixture.json` | tool; k6 reads `FIXTURE` and `TOKEN_SERVER` (`http://127.0.0.1:5999`) too |
| `DURATION`, `LISTENERS`, `SEND_RATE`, `READ_RATE`, `MESSAGE_LENGTH`, `HISTORY_PAGES`, `PAGE_SIZE` | 60s, 10, 5, 5, 80, 3, 50 | `messaging` |
| `STORM_RATE`, `STORM_PEAK`, `CATCHUP_LAG`, `CATCHUP_HUB` | 20, 30s, 100, true | `catch-up` |

Pass k6 variables with `-e NAME=value` or the shell environment. Defaults are sized for a laptop.

**5,000-user profile:** `profiles/target-5000.env` (100 organizations of 50 members, 5,000 live connections, 15 minutes). Run it on proper hardware: a dedicated stack and a separate generator machine. Setup limits: an account creates 5 organizations a day, an owner sends 50 invitations a day (hence at most 49 members per organization), and Keycloak sign-in of 5,000 users takes a while (`CONCURRENCY`).

## Not covered yet

- CI runs no k6 job: it would need the k6 binary (a third-party setup action to pin and review) and a new required-check path in the workflows. Add `k6 inspect` to CI together with the first real load environment.
- Search, presence, notifications and files do not exist in Phase 1.
- Neither scenario has been run at full size yet; the 5,000-user profile is a plan for proper hardware. Laptop runs are recorded in `results/` (first run: [2026-11-24-laptop.md](results/2026-11-24-laptop.md), k6 v2.4.0).
