# CellManagement (control plane)

Contract: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md) ("Cell ↔ control plane link"), protocol [cell_link.proto](../../../proto/backroom/controlplane/v1/cell_link.proto). **Human review required** for cell registration and authentication.

## Responsibility

- Cell registry (type, region, public URL, version, status, last heartbeat).
- Registration: one-time registration key → Keycloak client `cell-{cellId}` with the cell's public key (signed-JWT client authentication) and client scope `cell:{cellId}` with an audience mapper for user tokens.
- `CellLinkService`:
  - cell id from the token's `azp`; reject a mismatching `Hello.cell_id`;
  - one stream per cell: a new stream supersedes the old one; close the stream when the cell's token expires;
  - resend every unacknowledged downstream message in sequence order after `Hello`; skip messages whose type the cell's version does not support;
  - de-duplicate upstream messages by `message_id`, dispatch them to `ICellMessageHandler<T>`, commit, then `Ack`; a message naming an organization not mapped to the authenticated cell gets a non-retryable `Nack` `directory.organization_not_in_cell` and a security log event;
  - route `Ack`/`Nack` of downstream messages to `ICellMessageOutcomeHandler<T>`; retryable `Nack` → backoff; non-retryable → `rejected`;
  - `FullSync` from `IDirectoryQueries` and Identity's revoked sessions.
- `ICellLinkOutbox.EnqueueAsync(cellId, message)` in `CellManagement.Contracts`, written in the caller's transaction.
- Operations panel data: `GET /api/v1/ops/cells` (pending and rejected counts, link status, heartbeat), and an alert when a downstream message stays unacknowledged for more than 2 minutes.

## Owns

- Schema `cell_management`: `cells`, `registration_keys` (hashes), `outbound_messages` (payloads deleted once acknowledged), `inbound_messages`.

## Rules

- The control plane never opens connections to cells.
- No organization content passes through the link; the protocol has no message type for it.
- `.proto` changes follow the compatibility rules in the file; `buf breaking` must pass.

## Status

- Implemented: registry tables, `POST /api/v1/ops/cells` (pending cell and one-time key), `POST /api/v1/cells/register` (claim → Keycloak client and scope → complete), the development seeder of the AppHost's cells, and the Keycloak administration as `control-plane-admin`.
- Link endpoint (`CellLinkService.Connect`, gRPC, policy `cell`): only a registered cell's own token (`azp = cell-{id}`, no session); `Hello` within 10 s and naming the token's cell; `Welcome`; heartbeats update `version` and `last_heartbeat_at`; a new stream supersedes the old one in this instance (`Aborted`); the stream ends at token expiry (`Unauthenticated`). Durable messages get a retryable `Nack` `link.not_ready` until their handlers exist. Messages are limited to 4 MB. `FullSync` is not implemented yet. Supersession is per control-plane instance; several instances need a shared owner (later).
- Downstream delivery: `ICellLinkOutbox.EnqueueAsync` writes `outbound_messages` in the caller's unit of work (payload at most 64 KB, trace context kept). While a cell is connected its endpoint leases due messages every second in one `UPDATE … RETURNING` (partial index), at most 100 unanswered per stream, and sends them in sequence order; a lease belongs to its stream and lasts 30 s: only a newer stream (stream ids are UUIDv7) takes over a lease before it runs out, and taking over any lease (a send without an answer) counts as an attempt. Only answers to messages sent on the stream are settled (more than 300 unknown answers in a minute close it with `ResourceExhausted`). `Ack` and non-retryable `Nack` call `ICellMessageOutcomeHandler<T>` and settle the message in one unit of work with a `cell-link settle` span linked to the enqueuing trace, deleting the payload; a retryable `Nack` or a failing settlement delays the next attempt (2 s doubling to 5 min, one `UPDATE`); when our handler fails after the cell answered, the cell's first answer is kept (`cell_answer`), the message is not sent again, and the settlement is retried here with that answer. After 50 attempts (each retry committed on its own) a message the cell never answered is rejected with `delivery_expired` and its handler told; one it answered is closed as `handler_failed` without calling the handler again (critical log; operations alert later). Either way the payload is deleted. Failures here are logged with the exception type only and never end the stream. A message in backoff can be overtaken (contract, "Delivery"). Not yet: skipping types the cell's version does not support (all versions support all types today), the 2-minute unacknowledged alert, LISTEN/NOTIFY instead of polling (needed with several instances), a retention job for settled rows, and expiry of messages for cells that stay offline (attempts count only while connected).
- Upstream delivery: a durable message needs a UUID `message_id` and valid, non-empty organization ids, at most 10,000 (else non-retryable `invalid_message_id` / `invalid_organization_id` / `too_many_organizations`); every organization it names must be mapped to the sending cell by `ICellOrganizationScope` (implemented by Directory), else non-retryable `directory.organization_not_in_cell` and a security log; this check comes before the handler check, so it is logged even while a type has no handler. A type without an `ICellMessageHandler<T>` (or no scope registered) gets a retryable `link.not_ready`. Otherwise `inbound_messages` records `(cell_id, message_id)` and the handlers run in one unit of work, then `Ack`; a duplicate is acknowledged without handling. More than 300 refused messages a minute close the stream. The scope check runs before the duplicate check and outside the unit of work: fine while organizations never change cells (moving one needs both in one transaction). A handler refuses for good with `CellMessageRejectedException` (code checked against `[a-z0-9_.]{1,64}`); any other failure (also of the scope check) is a retryable `processing_failed`, logged with the exception type only. Not yet: retention of `inbound_messages` (dedupe window); a per-stream rate limit for durable upstream messages (today only refusals are limited; retryable answers such as `link.not_ready` still cost one scope query each, and the authenticated cell's own backoff bounds them).
- Shared cells: anonymous `GET /api/v1/cells/shared` (registered shared cells' ids only, oldest registration first, `Cache-Control: public, max-age=300`, 60 requests a minute per client network); clients request these scopes at sign-in. At most `CellManagement:SharedCells:Max` shared cells (default 20, and at most 20 outside Development, until sign-in uses pushed authorization requests), each id at most 16 characters; a pending shared cell holds a slot only while its key can still be used; provisioning refuses more with `cell_management.shared_cell_limit`. Two racing additions past the limit are not guarded (operators add cells one at a time).
- Contracts: `ICellCatalog` (registered cells' public URLs by id, at most 500 per lookup), used by Directory.
- Projects: Contracts, Application (registration and provisioning), Infrastructure (hand-written SQL, migrations, Keycloak admin client), Presentation (endpoints, registration rate limit). No Domain project yet.
- Next: `FullSync`, `GET /api/v1/ops/cells`, the alerts and retention jobs listed above.
