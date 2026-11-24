# Presence (cell)

Contract: [docs/contracts/phase-2.md](../../../../docs/contracts/phase-2.md) (flow 9 "Presence and status", "Live events", "Data", owner decision 16). Technical module: Contracts, Infrastructure, Presentation; no Domain, no PostgreSQL schema. **Human review required** (presence is visible across people; privacy rules).

## Responsibility

- Automatic presence per cell: online, idle (no input for 10 minutes, or the client reports idle), offline (no connection to the cell for 60 seconds). Computed from the hub's connections, never stored in PostgreSQL, no history (no daily totals, no last-seen).
- The cell's copy of each account's manual status (do not disturb, custom status) and time zone, from the link (`AccountStatusChanged`, `FullSync`).
- `IPresenceReader` for other modules and `GET /api/v1/orgs/{orgId}/presence?accountIds=` (member; at most 200 comma-separated ids; state and status only).
- `PresenceChanged` to the presence groups of people watched in an organization (never the organization group), at most one per person per 5 seconds, coalesced.

## Owns

- Valkey keys under `backroom:cell:{cellId}:presence:`, all with expiry: `c:{account}` (open connections: `{expiresAt}:{lastActiveAt}:{idle}` per connection id), `p:{account}` (last published state and status version, next allowed event time), `s:{account}` (status copy: `v`, `dnd`, `emoji`, `text`, `exp`, `tz`; kept 7 days, renewed by every `FullSync`), `w:{account}` (organizations where someone watches the person, entries expire after 10 minutes unless renewed), `due` (who must be evaluated again and when). No tables.

## Contracts (Presence.Contracts)

- `IPresenceReader.ReadAsync(accountIds)`: in the current unit of work; accounts that are not active members of that organization are left out (`IOrganizationMembershipCheck.SelectActiveMembersAsync`, one statement under RLS). Never the time zone.
- `IAccountStatusSink.ApplyAsync(copy)`: stores only a newer version (Lua compare-and-set); throws when Valkey fails, so the link retries.
- `IPresenceTracker` (the hub, app role) and `IPresenceMemberships.ForgetAsync(org, account)` (the member-removed consumer). Realtime registers no-op fallbacks, replaced by `AddPresence` / `AddPresenceTracking`.

## Depends on

- Realtime.Contracts (`ILiveEventPublisher`, `PresenceChangedPayload`); BuildingBlocks (`IOrganizationMembershipCheck`).

## Rules

- Owner decision 16: presence lives in Valkey outside RLS; the organization context comes from authentication (route plus membership check, or the hub's membership check); only members of the same organization see it. Watches are accepted only for people who are active members of the watcher's organization, so events reach only that organization's groups.
- Never platform, device or time zone in any answer or event. Logs carry account and organization ids only, never emoji, text or zone.
- Times come from the caller's `TimeProvider` (passed to the Lua scripts), never Valkey's clock; Valkey's expiries only clean up. Tests drive everything with `FakeTimeProvider`.

## Status

- Implemented (Phase 2 step 28), behind `Features:Presence` (on in development): `AddPresence` (every role: options, own Valkey multiplexer, store, sink, reader, memberships), `AddPresenceTracking` (app role: `PresenceTracker`, `PresenceSweeper`), `MapPresenceEndpoints`.
- **Evaluation** (`PresenceScripts.Evaluate`, atomic): prunes expired connections, computes the state, compares state and status version with what was published; publishes only after the event interval (else the person is due at its end), and puts the person in `due` at the next expiry or idle time. **Sweeper** (each app node, every second): renews its connections every 20 s and its watches at half their lifetime, then claims at most 500 due people with a 30 s lease (one node evaluates each). Every limit is in `PresenceOptions` (validated at startup).
- **Cost**: per online person one evaluation per ~40 s plus one per change; reads are one pipelined round trip; no fan-out to a whole organization.
- Tests: `Backroom.Modules.Realtime.IntegrationTests` (`PresenceTests`, `PresenceDisabledTests`: two nodes, one Valkey, fake clock: online, idle, offline after the grace, activity throttle, coalescing, status versions, org isolation, forget on removal, no status text in logs, payload fields).
- Not yet: the off-hours check and do-not-disturb reads for Notifications (step 29), watching from the web app's member lists (DMs arrive with step 8).
- **Deploy phase**: the Valkey ACL must allow the `presence:*` keys and the scripts (`EVAL`/`EVALSHA`) for the cell's nodes only.
