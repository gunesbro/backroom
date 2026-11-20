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
