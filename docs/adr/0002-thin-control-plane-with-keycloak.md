# 0002. Thin control plane with Keycloak for identity

- Status: Accepted
- Date: 2026-10-25

## Context

A person has one global account and can belong to many organizations, each possibly in a different cell or region. Something must know who the person is, which organizations they belong to and where those organizations live. Identity needs OIDC, SAML, two-factor authentication, social login and per-organization SSO. ASP.NET Core Identity alone offers neither SAML nor SSO brokering; Duende IdentityServer requires a paid license.

## Decision

- A single, thin control plane holds only cross-organization information: identity and sessions, the organization directory and cell routing, invitation resolution, licenses and plans, the activity and integration catalog, and the hybrid release channel.
- It never stores messages, files, meetings, calendars or any other organization content.
- Identity is provided by Keycloak (OIDC, SAML, 2FA, social login, Organizations feature for per-organization SSO). Login screens are themed in React with Keycloakify.
- After login the client receives the cell addresses of the person's organizations and connects to each cell directly.
- The operations panel for our team is part of the control plane and shows only counts and health, never customer content.

## Consequences

- Cells validate identity tokens locally; if the control plane is briefly unreachable, open connections keep working and only new logins and invitation resolution wait.
- Logout propagates from Keycloak back-channel logout to the control plane and from there to all cells (ADR 0003).
- The client never hard-codes the control plane address; it comes from configuration, so a fully on-premises installation remains possible later.
- Keycloak becomes a critical dependency and is run with its own database and backups.

## Implementation (Phase 1, step 2)

- Development realm: `backend/src/Aspire/Backroom.AppHost/keycloak-dev/backroom-realm.json`, imported by the AppHost on first start. **For development only**: it has test users, localhost redirect URIs and `sslRequired: external`; production realms are configured in the deploy phase. Public clients `web` and `ops` (authorization code with PKCE S256 only; no password grant, no implicit flow), access tokens 5 minutes, rotating refresh tokens, brute-force protection, email verification, Turkish and English login pages, SMTP to Mailpit (ADR 0008). The realm lists Keycloak 26.8.1's built-in client scopes explicitly (exported from Keycloak), because a realm file that defines any client scope gets no built-in ones.
- Test users `alice@example.test` and `bob@example.test`. Their password is not in the repository: the realm file has placeholders, and the AppHost fills them from a generated parameter (`dev-user-password`, kept in the AppHost's user secrets and shown in the dashboard). Imported without that variable, the literal placeholder would become the password, which is one more reason the file is never used outside development.
- **One audience per token.** The audience `control-plane` comes from the optional client scope `control-plane`, which a client requests explicitly (`scope=openid control-plane`); cell tokens will come from the scope `cell:{cellId}` created at cell registration. A client requests one target per token, and every service accepts only tokens whose single audience is its own, so a token with several targets is accepted by none and a token given to one cell cannot be replayed elsewhere.
- Hosts: `AddAccessTokenAuthentication(audience, allowedClients)` (BuildingBlocks.Presentation) validates tokens locally against the realm's JWKS: issuer = `Authentication:Authority`, exactly the host's audience, lifetime (30 s clock skew), RS256 only, payload `typ` = `Bearer` (ID, refresh and logout tokens are rejected), `azp` among the host's clients (control plane: `web`, `ops`; cells: `web`), a session (`sid`), and `sub` an account id. Service-account tokens (client credentials) have no session and are rejected; the cell link will authenticate with its own scheme. Every endpoint requires a signed-in account unless it allows anonymous access (only health endpoints do). Outside Development the authority must use HTTPS and failure details are not sent in `WWW-Authenticate`. `ICurrentAccount` gives handlers the account and session id.
- Policy `operations`: a token of the `ops` client, realm role `ops` (only the `ops` client may carry it: both clients have `fullScopeAllowed` off and only `ops` has a scope mapping for the role), and `amr` containing `otp`. Keycloak's AMR mapper emits a value only for flow executions whose authenticator configuration sets `default.reference.value` (`otp` on the OTP form) and a positive `default.reference.maxAge`; after that age, refreshed tokens lose the value and the policy denies access. This configuration comes with the operations panel; until then the policy denies everyone.
- Tests: forged and misdirected tokens (other or additional audience, other issuer or key, `alg: none`, HS256, expired, not yet valid, ID and refresh tokens, service-account tokens, other clients) and the realm imported into a real Keycloak, whose tokens open the control plane only when requested for it and never a cell.
