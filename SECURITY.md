# Security policy

## Reporting a vulnerability

Please do not open a public issue, discussion or pull request for security problems. Report them privately through GitHub: open the repository's Security tab and choose "Report a vulnerability" (private vulnerability reporting).

Include:

- what you found and which component it affects;
- steps to reproduce it, or a proof of concept;
- the impact you expect;
- the version or commit you tested.

Backroom is maintained by one person, so responses are best effort. We aim to acknowledge a report within about a week and to agree on a disclosure date with you once a fix is available.

## Supported versions

There is no stable release yet. Only the `main` branch is supported; fixes land there.

## Scope

The application code in this repository: the control plane, cells, web, operations panel and desktop app.
Development-only configuration (for example Mailpit, local LiveKit keys and open local S3 storage) is
intentionally permissive and is not a vulnerability unless it can affect a production deployment.

## Known advisories (accepted, reviewed)

| Advisory | Package | Why it stays | Revisit |
| --- | --- | --- | --- |
| [GHSA-wrw7-89jp-8q8g](https://github.com/advisories/GHSA-wrw7-89jp-8q8g) (medium) | `glib` 0.18.5 (Rust, Linux desktop only, via Tauri's GTK3 stack) | Unsoundness in `glib::VariantStrIter`'s iterator implementations. Our code never uses glib directly; the fix is in glib 0.20, which needs Tauri's move to GTK4. | When Tauri or wry update their GTK bindings; Dependabot keeps the alert open. |

## Practices

- Secrets never enter the repository; gitleaks scans the full history on every pull request.
- Dependencies are pinned (lock files for .NET, pnpm and Cargo; container images and GitHub Actions by version or SHA) and updated through reviewed Dependabot pull requests.
- Privacy and security rules are listed in [CLAUDE.md](CLAUDE.md) and enforced by tests.
