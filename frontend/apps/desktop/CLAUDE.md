# Desktop app (Tauri shell)

Contract: [docs/contracts/phase-1.md](../../../docs/contracts/phase-1.md) ("Identity and tokens", flow 1, desktop). **Human review required** for `src-tauri/src/auth`.

## Responsibility

- Hosts the web app (`apps/web`) in the main window; the window loads only the app's own origin.
- Signs in through the system browser (RFC 8252) and keeps the session. The refresh token never reaches the web view; the web view asks for access tokens, one target at a time.

## Sign-in (`src-tauri/src/auth`)

- `config.rs`: the issuer is fixed at build time (`BACKROOM_AUTH_ISSUER`); release builds refuse anything but `https://<host name>/realms/<name>` (`issuer.rs`, shared with `build.rs`: no IP address, localhost, trailing slash, query, fragment or user info); client `desktop`.
- `loopback.rs`: listener on `127.0.0.1`, port 0, path `/callback`; wrong path, method or `state` → 404/400 and keep waiting; the expected `state` with another or no `iss` ends the attempt; connections are read side by side (at most 8, 8 KiB and 2 s each), so a silent one cannot delay the redirect; closes after 10 minutes. Pages from `page.rs` load nothing else (CSP `default-src 'none'`, `no-store`).
- `session.rs`: one lock for every token request (refresh tokens are single-use); the code exchange's all-audience access token is discarded; every access token names one target and the answer's `scope` is checked; `invalid_grant` clears the session; sign-out calls Keycloak's logout and keeps the token only as "sign-out pending" when Keycloak cannot be reached, retried at start and before a sign-in (which waits until it went through); a sign-in into another Keycloak session logs out the one it replaces. Store calls run on blocking threads (unlock prompts).
- `store.rs`: the OS secure store (`keyring`), split into entries of at most 2 KB (one Windows credential holds 2,560 bytes; a refresh token with 30 cell scopes measured about 2.4 KB), with a header of entry count, length and hash so a half-finished write is never read. Windows entries use local persistence, never the default that roams with the user profile. No store: the session lives in memory only, never in a file.
- `tokens.rs`: token and logout requests over HTTPS only, no redirects, 15 s timeout; `Secret` prints redacted.
- Commands: `auth_status` (the account or `null`), `sign_in { cells, locale }`, `reopen_sign_in`, `sign_in_link`, `cancel_sign_in`, `access_token { target }`, `forget_session` (this device only, after the server rejected a fresh token), `sign_out` (also ends the Keycloak session). Errors are codes (`signed_out`, `scope_not_granted`, `invalid_scope`, `invalid_request`, `cancelled`, `timed_out`, `denied`, `network`, `server`, `listener`).
- One running instance (`tauri-plugin-single-instance`): a second start brings the window forward.
- URL scheme (`tauri-plugin-deep-link`, `plugins.deep-link` in `tauri.conf.json`, a placeholder like the product name): the completion page asks the browser once to open the app and keeps a button. Any web page can call the scheme, so it only starts the app or brings it forward and every parameter is ignored. Installers register it, an AppImage registers itself at start, and a development build does so only with `BACKROOM_REGISTER_URL_SCHEME` set (README, "Frontend"). A failed registration is logged and never stops the app.
- The commands are declared in `build.rs` (an app manifest), so only capabilities that list them can call them: `capabilities/default.json` grants them to the `main` window. A new command needs both.
- Token requests honour the system proxy and `HTTPS_PROXY` (companies need this).

## Commands

```powershell
# From frontend/apps/desktop/src-tauri
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
# A release build needs the issuer, for example:
$env:BACKROOM_AUTH_ISSUER = "https://id.example.test/realms/backroom"
```

## Not yet

- Release configuration: the control plane's origin in `VITE_CONTROL_PLANE_URL` and the desktop CSP `connect-src` (until then a release build signs in without the shared cells' scopes).
