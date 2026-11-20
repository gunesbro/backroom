# 0009. Keycloak: account settings in our app, unused features off

- Status: Accepted
- Date: 2026-10-30

## Context

Keycloak ships an account console (`/realms/{realm}/account`) with its own REST API, and an impersonation feature that lets an administrator act as any user. The product's design has its own account settings screens (devices and sessions, profile and theme, password, two-step verification). People should meet one settings screen in one design, and the product must stay easy to upgrade.

Impersonation conflicts with the privacy rules: no role, owner or operator included, may read other people's messages, and acting as a person would give exactly that access.

Many products that put Keycloak behind a customer-facing application keep account settings in the application and use Keycloak only for the security-sensitive steps; Keycloak supports this with application-initiated actions.

## Decision

1. The Keycloak features `account` (account console), `account-api` (its REST API) and `impersonation` are disabled in every environment: the AppHost and the test containers set `KC_FEATURES_DISABLED`; a deployment that starts Keycloak with `--optimized` bakes the same list into its image (`kc.sh build --features-disabled=...`), because it is a build-time option. Token exchange v1, the other way to act as another user, stays disabled (Keycloak's default).
2. Account settings are screens of our app. Security-sensitive steps run on Keycloak's own pages, with our login theme, through application-initiated actions (`kc_action`): changing the password, setting up two-step verification, updating the email, deleting the account. The app never sees a password. Account deletion needs the realm's `delete_account` required action enabled and the `account` client's `delete-account` role granted, which is why the built-in `account` client stays.
3. Data such as the list of sessions and devices comes from our backend (the identity module and the cell's session revocation), not from Keycloak's account API.
4. Our login theme overrides Keycloak's supported extension points (templates, message bundles, styles). Each copied template names its keycloak.v2 source and version, so it is compared on every Keycloak upgrade.

## Consequences

- Fewer exposed endpoints, and no second account UI to theme or keep in step with Keycloak releases.
- Our settings screens must be built before people can see their sessions or devices; until then the sign-in pages offer password reset only.
- A privacy test (`KeycloakRealmTests`) checks that the three features and token exchange v1 are off and that the account console returns 404.
- The built-in account clients remain and point at the disabled console: emails an administrator sends (execute actions, verify email) must name `client_id=web` and a redirect back to the app, so the "back to application" link does not lead to a 404.
- Re-enabling any of them needs a new ADR.
