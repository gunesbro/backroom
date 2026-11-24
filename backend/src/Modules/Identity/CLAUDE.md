# Identity (control plane)

Contract: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md) ("Identity and tokens", flow 5). **Human review required** for every change in this module.

## Responsibility

- Keycloak integration for the control plane: token validation settings (issuer, JWKS, audience `control-plane`) shared with the host.
- Keycloak back-channel logout endpoint: verifies the logout token (signature, issuer, audience, `events` claim), records the revocation and enqueues `SessionRevoked` (with `keep_until` = revocation time + longest token lifetime) for every cell where the account has a membership.
- Provides revoked sessions for `FullSync`, limited to accounts that are members in the requesting cell.
- The `ops` client policy: realm role `ops` and two-factor authentication (`amr` contains `otp`).
- Later phases: device and session list, new-device email, organization SSO.

## Realm settings

- The realm is configured in `backend/src/Aspire/Backroom.AppHost/keycloak-dev/backroom-realm.json` (development; deployments apply the same settings); the login theme is in `keycloak-themes/` beside it. `KeycloakRealmTests` (BuildingBlocks.IntegrationTests) pin what the contract relies on: session lifetimes and "Remember me", single-use refresh tokens, no offline tokens, the clients `web`, `desktop` (loopback redirect) and `ops`, refresh narrowing to granted scopes, the verification email cooldown, disabled Keycloak features and the copied login templates.

## Owns

- Schema `identity`: `revoked_sessions (session_id, account_id, revoked_at, keep_until)`; rows are deleted after `keep_until`.

## Depends on

- `ICellLinkOutbox` (CellManagement.Contracts) to enqueue `SessionRevoked`.
- `IDirectoryQueries` (Directory.Contracts) for the cells where an account has a membership.

## Rules

- Keycloak holds accounts and credentials; this module never stores passwords or tokens.
- Log account ids only, never emails or names.

## Status

- Implemented (Phase 1 step 6.3): projects Contracts (`IRevokedSessionQueries`, `RevokedSession`, `SessionRevocationPolicy`), Application, Infrastructure, Presentation; schema `identity`, table `revoked_sessions (session_id, account_id, revoked_at, keep_until)`, primary key `(session_id, account_id)`; an empty `session_id` stands for every session of the account.
- **Endpoint** `POST /internal/keycloak/backchannel-logout` (anonymous, form `logout_token`, at most 16 KB, 3,000 calls per network and minute). Only Keycloak calls it, from inside the deployment: the proxy must not publish `/internal`. `LogoutTokenValidator` (Presentation) checks with the same JWT settings and JWKS cache as access tokens (the authentication handler's configuration manager; one key refresh when the key id is unknown): RS256, issuer, audience one of `web`/`desktop`/`ops`, `exp` if present; plus `iat` within 2 minutes (and at most the clock skew ahead), `jti`, `events` with `http://schemas.openid.net/event/backchannel-logout` as an object, no `nonce`, not an access token (`typ` `Bearer`), `sub` an account id (required: without it the cells cannot be found) and `sid` (required, printable, at most 128: every client has session-required, so a token without it is refused). Success answers `200` with `Cache-Control: no-store`; every refusal answers `400 {"error":"invalid_request","error_description":"The logout token is not valid."}` with the reason only in the log (`Security:` warning, no token content).
- **Recording** (`SessionRevocations`): in one unit of work the row is written and `SessionRevoked { account_id, session_id?, revoked_at, keep_until }` is queued through `ICellLinkOutbox` for every cell where `IDirectoryQueries.ListCellsOfAccountAsync` finds a pending or active membership (organization not failed). `keep_until = revoked_at + SessionRevocationPolicy.KeepFor` = 5 min (longest access token lifetime, pinned by `KeycloakRealmTests`) + 30 s (the cells' clock skew). Idempotent per `(sid, account)`: a repeated logout token inserts nothing and sends nothing. Revoking every session of an account (`SessionRevocations.RevokeAsync` with no session id; the endpoint never does) replaces the older row and is sent again.
- **Lost revocations**: Keycloak does not retry a back-channel call. If handling a valid token fails (database down) the endpoint answers 500, counts `backroom.identity.logout_failures` (alert on any) and logs an error with the account id only; the revocation is lost and the session stays usable until its access token expires, at most 5 minutes (the bound of the whole design).
- **Cleanup**: `RevokedSessionCleanupWorker` (every minute, random start delay, like Directory's invitation jobs; application role, because the table is not organization-scoped) deletes rows past `keep_until` in batches of 500 (`FOR UPDATE SKIP LOCKED`, at most 20 batches per run).
- **FullSync**: `IRevokedSessionQueries.ListForCellAsync` reads the rows in force newest first in keyset pages of 2,000 (at most 10 pages), filters each page to the accounts with a membership in the asking cell (`IDirectoryQueries.SelectMembersOfCellAsync`; modules cannot join each other's tables) and keeps at most 10,000, so a cell keeps its members' newest revocations.
- Tests: `Backroom.Modules.Identity.IntegrationTests` (each invalid token kind, storage, fan-out to the right cells only, idempotency, cleanup, FullSync query, and one test against a real Keycloak: the container calls the test host as `host.docker.internal`).
- Not yet: the session list, the new-device email, organization SSO.

## Account status (Phase 2 step 28)

**Human review required.** Contract: [phase-2.md](../../../../docs/contracts/phase-2.md), flow 9, "Control plane", "Cell ↔ control plane link".

- Table `identity.account_status (account_id, dnd_until, custom_emoji, custom_text, custom_expires_at, time_zone, version, updated_at)`: one row per account, replaced on change (no history), no RLS (account-level, no organization content).
- `GET` / `PUT /api/v1/me/status { dndUntil?, customStatus? { emoji?, text?, expiresAt? }, timeZone }` behind `Features:Presence`: own status only, `no-store`; `PUT` replaces the whole status. Rules (`AccountStatusRules`): do not disturb in the future and within 7 days; custom status with one emoji (a single grapheme cluster of emoji code points), up to 100 characters of text (NFC, trimmed, no control, line separator, bidi or other format characters except joiners) or both, ending within 30 days; an IANA time zone the runtime knows. Every failure is `400 identity.status_invalid`; 30 changes per account and minute (`429 identity.rate_limited`, sliding window, in memory per instance). Expired parts come back null.
- Fan-out (`AccountStatuses`): in one unit of work the row is upserted (version + 1) and `AccountStatusChanged` with the whole status is queued for every cell where the account has a pending or active membership. `FullSync` carries the statuses of the asking cell's members (`IAccountStatusQueries`: Directory's members of the cell in keyset pages of 5,000, statuses by primary key on their own connection; at most 10,000). Logs carry the account id and version only.
- Tests: `AccountStatusTests` (Identity), `AccountStatusLinkTests` (CellManagement: delivery, refusal, FullSync for members only).
