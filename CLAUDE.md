# Backroom: agent guide

A privacy-first team workspace that combines team messaging, a virtual office and drop-in voice and video rooms. First release targets web and desktop (Tauri); mobile comes later. The product is called Backroom; the logo is not decided yet.

## Sources of truth

- Requirements, architecture, module plan, folder layout, test strategy and roadmap: [docs/requirements.md](docs/requirements.md) (Turkish).
- That file is a snapshot of the living document in Claude Docs: https://claude.ai/artifact/6BPW2Ei7EHJZcTUd3ExLKX. If the two disagree, the living document wins.
- Architecture decisions: [docs/adr/](docs/adr/).
- UI mockups: [docs/design/](docs/design/). Screen texts are taken verbatim from the sources there.

Read the relevant section of the requirements before starting any task. If a task conflicts with one of the six principles (privacy first, no pressure, transparency, fluency, provider independence, portability), stop and raise it.

## Language

- Talk to the user in Turkish.
- Everything in the repository is English: code, identifiers, code comments, commit messages, log messages, CLAUDE.md files, READMEs and ADRs.
- Turkish appears only in UI translation files (and in the Turkish design and requirements documents under `docs/`).
- Never name other products or companies we compete with (team chat, video meeting or virtual office products) anywhere visible: code, docs, UI texts, commit messages, branch names, PR titles, descriptions and comments. Describe features generically. The repository is public. Technologies we use or build on (Keycloak, LiveKit, Envoy and so on) may be named.

## License

- The repository is source-available under PolyForm Noncommercial 1.0.0 (see `LICENSE`). Never call it open source. Commercial use needs a separate license from the owner.
- Outside contributions require signing the CLA (`CLA.md`, collected through cla-assistant.io on the first PR).

## Working rules

- Small units of work: one PR, one behavior. Every task happens on its own branch and is merged through a PR. Never push to `main` directly.
- Contract first: a module's interface, events and schema are written and approved before its implementation.
- Each module is owned by one agent, which writes only to that module's folder and database schema. Every module folder has a short CLAUDE.md (responsibility, owned tables, published events, dependencies); read it first.
- Every change is reviewed by a separate reviewer agent that did not write it.
- Every PR also gets a security review (by that reviewer or a dedicated one) covering: secrets and logging of sensitive data; injection (SQL, shell, script, XSS, workflow expressions); authentication and authorization, including organization isolation and RLS; privacy rules; input validation and limits; cryptography and token handling; insecure defaults that could reach production; dependency and supply-chain changes; CI permissions. Findings rated medium or higher are fixed before merging; low findings are fixed or recorded in the PR with a reason.
- Every PR also gets a performance and resource review against the rules in "Performance and resources". A change that breaks the budget or does not scale is not merged; changes to hot paths report measurements in the PR.
- Human review is required for code touching identity, permissions, encryption, guest access and email safeguards. Say so in the PR description.
- Unfinished features are merged behind a feature flag. `main` must always build and pass tests.
- Definition of done: unit and integration tests, architecture tests, OpenTelemetry traces, no personal data in logs, accessibility, Turkish and English texts, performance budget met.
- Every bug fix comes with a test that reproduces the bug.
- Ask the user before installing system software, changing system settings or deleting files.

## Security tooling

- CI: gitleaks (full history), Semgrep CE (SAST for C#, TypeScript, React, GitHub Actions, package manager settings, secrets), .NET security and performance analyzers as errors (`AnalysisModeSecurity=All`, `AnalysisModePerformance=All`), web bundle-size budget, NuGet audit including transitive packages, `buf breaking` for the cell link, actionlint, `cargo clippy -D warnings` for the desktop shell.
- The desktop shell (Rust) is formatted, linted and tested on Windows and Linux in every PR that touches `frontend/apps/desktop` (`desktop-check.yml`; only `main` saves its cache). Installers and macOS are built only by the manually triggered "Desktop packages" workflow: run it when a change could break packaging or Linux-only code.
- GitHub: Dependabot alerts and security updates are on; version updates wait 7 days (cooldown). Secret scanning with push protection, CodeQL (default setup) and private vulnerability reporting are on. When Dependabot opens several npm PRs that touch `pnpm-lock.yaml`, combine them into one PR with a regenerated lock file, because each merge makes the others conflict.
- pnpm refuses packages younger than 7 days (`minimumReleaseAge`), exotic transitive sources and trust downgrades; exceptions go into `pnpm-workspace.yaml` with a reason. When an urgent security fix is younger than 7 days (Dependabot security PRs skip the cooldown and would fail), add that exact version to `minimumReleaseAgeExclude` in the same PR and remove it once it is older.
- Cargo has no such setting, but the same 7-day rule applies to crates: check the release date on crates.io of every crate a change adds to `Cargo.lock`, transitive ones included, move a young one back with `cargo update -p <crate> --precise <version>`, and pin a direct dependency with `=` while its newest release is too young.
- Semgrep rulesets are fetched when the job runs, so new rules can flag unchanged code; fix the finding or suppress it inline with a `nosemgrep` comment and a reason.
- Hosts allow `localhost`, `127.0.0.1` and `[::1]` (`AllowedHosts`); deployments set the real host names through configuration (`AllowedHosts` environment variable).
- CORS: each host allows only the browser origins in `Cors:AllowedOrigins` (exact origins, no wildcards or paths; https except loopback http and `tauri://localhost`; empty means none; environment variable `Cors__AllowedOrigins__0`, `__1`, ...). APIs take bearer tokens only, so credentials are never allowed. Deployments set the real web origins; the AppHost sets the dev, ops and desktop ones. The desktop CSP `connect-src` must list the same cell origins.

## Performance and resources

Budget from the requirements ("Performans bütçesi"); work over budget is not done. Measured on a 4-core, 8 GB laptop on a home connection. Rows marked *proposal* are proposals in the requirements; the web bundle row is an engineering budget of this repository.

| Measure | Target |
| --- | --- |
| Desktop app start | < 1.5 s (last conversation drawn from the local cache) |
| Channel or DM switch | < 100 ms (cache first, then sync) |
| Join a room and hear audio | < 1 s (audio first, video after) |
| Scrolling and animation | 60 fps (long lists virtualized) |
| Desktop memory outside meetings | < 300 MB |
| Message visible on another client | p95 300 ms, same region (*proposal*) |
| Search, first page | p95 500 ms (*proposal*) |
| Web bundle (engineering budget) | initial ≤ 170 kB JS and ≤ 30 kB CSS, each lazy chunk ≤ 200 kB, all gzip (`frontend/bundle-budget.json`, checked in CI) |

Backend rules:

- Every query is bounded: keyset pagination (never offset on large tables), a `LIMIT`, and an index for every filter and sort used in a hot path. No N+1: load related data in one query or a batch.
- EF Core: `AsNoTracking` for reads, projections (`Select`) instead of whole entities, compiled queries or hand-written SQL for the hottest paths; no lazy loading.
- Async all the way: no `.Result`, `.Wait()` or sync I/O; pass `CancellationToken` everywhere.
- Logging uses source-generated `LoggerMessage` methods (CA1848); no string building in hot paths; prefer `Span`/pooled buffers where allocations show up in profiles.
- Every cache and in-memory collection has a size limit and an expiry; every input (payload size, list length, page size) has a maximum.
- Live events carry only what clients need (MessagePack); fan-out goes through groups, never loops over connections. SignalR sets `MaximumReceiveMessageSize`.
- Outgoing HTTP calls use `IHttpClientFactory` (typed clients); never `new HttpClient()` per call.
- Npgsql connection pools have an explicit `Max Pool Size` per role. The organization context is set transaction-locally (`set_config('app.org_id', ..., true)`); `AddDbContextPool` is used only if that state cannot leak between leases (a security rule too).
- The outbox relay reads in batches with `FOR UPDATE SKIP LOCKED` and a partial index on unpublished rows; consumers prefetch with a bounded window.
- .NET performance analyzers run as errors (`AnalysisModePerformance=All`). Hot paths get BenchmarkDotNet benchmarks; k6 load tests run at the end of each phase against the 5,000-user target.

Frontend and desktop rules:

- Long lists are virtualized (TanStack Virtual); heavy work runs in a Web Worker.
- Server state lives in TanStack Query with explicit cache keys; avoid re-renders (stable props, memoized selectors in Zustand, no context for fast-changing state).
- Code split by route and feature (`lazy` imports); new dependencies are checked for size before adding.
- The desktop app stays a thin shell; no background timers or polling without a reason; memory is measured in release builds and includes the WebView2/WebKit child processes.
- The local cache (IndexedDB, SQLite) has a size limit and evicts old data.
- Lists show sized thumbnails, never original images.

Deploy phase (not yet): precompressed static assets or compression at the proxy (no response compression for HTTPS responses carrying secrets, because of BREACH), `immutable` caching for hashed `/assets/*` and `no-cache` for `index.html`, HTTP/2 and HTTP/3, Valkey `maxmemory` and eviction policy.

## Privacy rules (product rules, enforced by tests)

No role, plan or organization setting can change these. A change that breaks one is not merged.

- No role, owner included, can read or export other people's messages. No admin UI or API endpoint does this.
- Private channel names and content, DMs and group DMs are visible only to members; they never leak into the audit log, search or notifications.
- Personal room conversations appear only in the room owner's calendar.
- Google/Outlook events and private events are visible only to their owner; a private meeting shows as "Busy" to admins.
- Admins see the status strip and platform, never daily totals, game-room time or device details.
- In anonymous polls the vote-to-person mapping is never shown to anyone, admins and the creator included, and is deleted when the poll closes.
- Game scores and activity results are not stored. Meeting chat is deleted permanently when the meeting ends.
- Integrations and activities cannot read channels or messages. Guests access only the meeting they were invited to.
- Every row carries `org_id` and PostgreSQL RLS is on. The organization context comes from authentication, never from a client-supplied value.
- Search, notifications and AI retrieval go through the same permission check as regular reads.
- Files are downloaded through short-lived signed URLs; access is re-checked on every request.
- Provider keys and other secrets are stored encrypted and never shown again after entry.
- Transparency: starting a recording or an AI note is announced to everyone, with an indicator while it runs. Every calendar view is written to the audit log, and people can see who can view their calendar.
- Invite links are unguessable, time-limited and use-limited.
- Every privacy rule has at least one automated test in the dedicated privacy test suite.
- Message, file and email content is never logged; personal data is masked before logging. Logs are structured and carry trace ID and organization ID.

## Secrets and email safety

- Keys, passwords and tokens never enter the repository. Use .NET user-secrets or a git-ignored `.env` locally.
- In the AppHost, every secret is an Aspire parameter: `builder.AddParameter("name", secret: true)` for values we receive (OAuth client secrets, cloud keys), or a generated, persisted parameter for values we create. Values live in the AppHost's user secrets or environment variables, never in code or `appsettings*.json`.
- Services read secrets through options validated at startup (`ValidateOnStart`); a missing secret stops the service instead of falling back to a default.
- CI runs gitleaks over the whole git history on every PR, and an architecture test rejects secret-looking keys with values in `appsettings*.json` and `launchSettings.json`.
- Outside production, all email goes to Mailpit. This is hard-coded, not configurable; organization SMTP settings are ignored. Real sending requires both the Production environment and an explicit "allow real sending" setting. Mailpit relay is never configured. Only the email module may use SMTP (checked by architecture tests). Test data uses `.test` addresses.
- The same applies to other outbound channels: outside production, webhooks, calendar invites and notifications go only to test accounts or fake recipients.

## Architecture rules

- Every external service sits behind our own interface and is selectable per organization: `IMeetingProvider`, `IEmailSender`, `IFileStorage`, `ICaptchaVerifier`, `ICalendarProvider`, `ISearchIndex`, `ILlmProvider`, `ISpeechToText`, `IEmbeddingProvider`, `IVectorStore`. No decision ties the system to one cloud provider.
- A module may reference only another module's `Contracts` project; every other type is `internal`. Layer rules are enforced by project references and architecture tests.
- A state-changing operation writes its event to the outbox table in the same transaction.
- Web features access each other only through their `index.ts` (enforced by ESLint).
- .NET package versions live only in `backend/Directory.Packages.props`; warnings are errors.

## Libraries

- Before adding a library, check its license, latest release date and adoption; write the result into the PR description.
- Code we ship or link must have permissive licenses (MIT, BSD, Apache-2.0, ISC, Zlib, Unicode and similar), because the project is also licensed commercially. Weak copyleft (MPL-2.0) is allowed only as an unmodified transitive dependency and must be listed in the PR when added. Build, test and CI tools, and services run as separate processes, are exempt. Strong copyleft (GPL, LGPL linked into shipped code, AGPL, EUPL), SSPL and other source-available licenses are never allowed in shipped code. Check transitive dependencies too.
- Avoid: MediatR v13+, AutoMapper v15+ (commercial license), MassTransit v9 (paid), ImageSharp (commercial license terms), FluentAssertions v8+ (commercial license, use AwesomeAssertions), Moq (use NSubstitute), Bitnami images, MinIO.
- Pin container image versions; never use `latest` (project convention, generalized from the Mailpit rule).
- Lock files are committed and enforced: `packages.lock.json` per .NET project (CI restores in locked mode; after changing `Directory.Packages.props` or a project's references run `dotnet restore` locally and commit the lock files), `pnpm-lock.yaml` (`--frozen-lockfile`) and `Cargo.lock` (`--locked`). NuGet packages come only from nuget.org (`backend/nuget.config` source mapping).
- Dependabot opens grouped weekly update PRs and immediate security PRs; review them like any other PR.
- Dependabot's NuGet PRs often update only some `packages.lock.json` files and fail the locked restore (`NU1004`). Replace them with one PR that bumps the same versions in `Directory.Packages.props` and regenerates every lock file (`dotnet restore backend/Backroom.slnx --force-evaluate`), then close them with a link to it. A .NET servicing release can be a security release: check `releases.json` and move the other Microsoft packages of that release along, and the SDK in `global.json` with them. Stay on the Microsoft package line that matches the target framework (10.x for .NET 10); a new major line comes with a deliberate framework upgrade.

## Repository layout

```text
backend/     .NET 10 solution (Aspire AppHost, hosts, building blocks, modules, tests)
frontend/    pnpm workspace (apps/web, apps/ops, apps/desktop, packages/*)
docs/        requirements, ADRs, design mockups, spike reports
load-tests/  k6 scenarios
deploy/      Compose and Helm (deploy phase)
```

Module layout and dependency rules are in the "Repo ve klasör yapısı" section of the requirements.

## Naming

- C#: one type per file, PascalCase, async methods end with `Async`, nullable enabled. Namespaces `Backroom.*`.
- TypeScript: kebab-case file names, strict mode. Packages `@backroom/*`.
- Database: one schema per module; snake_case tables and columns.
- Tests: `Behavior_Condition_Expected`, e.g. `SendMessage_WhenUserIsNotMember_ReturnsForbidden`.

## Testing

- Server: xUnit v3, AwesomeAssertions, NSubstitute, Testcontainers, Respawn, Verify, ArchUnitNET. Client: Vitest, Testing Library, MSW, Playwright, axe. Load: k6.
- Time-dependent code uses `TimeProvider` and is tested with `FakeTimeProvider`.
- Tests run under both the default culture and `tr-TR` to catch Turkish İ/ı bugs. The backend suite runs a second time under `tr-TR` in CI (`backend.yml`, guarded by `TestRunCultureTests`; `CultureScope` switches culture inside a test). The frontend tests (Vitest) do not have such a run yet, so frontend code compares and formats with an explicit locale.
- Tests are independent: each prepares its own data with test data builders; no shared mutable state.
- Tests never call real external services: email goes to Mailpit, meetings to local LiveKit, storage to SeaweedFS, captcha uses test keys.
- Flaky tests are quarantined and fixed the same week, never retried into passing.

## Common commands

Keep this list in sync with the code.

```powershell
# All commands run from the repository root.

# Backend (tests use Microsoft.Testing.Platform, configured in global.json)
dotnet build backend/Backroom.slnx
dotnet test --solution backend/Backroom.slnx

# Run everything locally with the Aspire dashboard at http://localhost:15000 (requires Docker Desktop)
# Add `$env:Backroom__Cells = "1"` first to run one cell instead of two (the default).
# Keycloak: https://localhost:8443, realm backroom; test users alice@example.test and bob@example.test,
# whose password is the dashboard's "dev-user-password" parameter.
dotnet run --project backend/src/Aspire/Backroom.AppHost

# Frontend (pnpm version comes from frontend/package.json "packageManager")
pnpm -C frontend install
pnpm -C frontend format:check
pnpm -C frontend lint
pnpm -C frontend typecheck
pnpm -C frontend test
pnpm -C frontend build          # web and ops; the desktop app is built by Tauri
pnpm -C frontend check:bundle   # bundle budget, after build
pnpm -C frontend desktop:dev    # desktop app with the web dev server (needs Rust and the C++ build tools; the URL scheme needs one registered start, see README)

# Load tests (k6 is installed separately; use a separate stack, never production; see load-tests/README.md)
node load-tests/tools/loadtest.mjs provision   # users, organizations, channels; then: ... serve
k6 run load-tests/scenarios/messaging.js        # also catch-up.js
```

Frontend notes:

- TypeScript is pinned to 6.0.x because typescript-eslint does not support TypeScript 7 yet; upgrade both together.
- Dependencies use exact versions (`saveExact` in `pnpm-workspace.yaml`); upgrades are deliberate PRs.
- Workspace packages (`@backroom/*`) export their TypeScript sources directly; there is no separate build step for them.
- The feature boundary is enforced by a local ESLint rule (`frontend/eslint-rules/feature-boundaries.js`, tested with ESLint's RuleTester); it resolves alias and relative imports, including escapes into sibling features.
- The `identifier` in `tauri.conf.json` (`io.github.gunesbro.backroom`) is fixed now; it is hard to change after release because it decides data folders and upgrade identity. `productName`, window titles and the HTML titles are still plain strings; before the first release they must come from the single branding setting the requirements call for.
- Desktop CSP is minimal; the cell API origins (`connect-src`, matching each cell's `Cors:AllowedOrigins` entry for the desktop origin), `wss:` for SignalR and LiveKit, and `blob:` for media must be added when those features arrive.
- The local Keycloak keeps its data in a Docker volume, so changes to the realm file (`backend/src/Aspire/Backroom.AppHost/keycloak-dev/backroom-realm.json`) are not re-imported. Apply them to the running realm with `kcadm.sh` inside the container, or reset the Keycloak volume (ask first: it deletes local accounts). `KeycloakRealmTests` import the file fresh, so they always test the file itself.

## Git

Project conventions (agreed in Phase 0):

- Branch names: `feat/…`, `fix/…`, `chore/…`, `docs/…`, `test/…`, `ci/…`, `perf/…`, `sec/…`, `spike/…`.
- PRs are squash-merged after review and the user's approval.
- The `main` ruleset requires a PR with one approving review, resolved conversations, linear history, squash merges only and the five `*-required` status checks; force pushes and deletion are blocked. Agents open PRs from the owner's account, which cannot approve its own PRs, so the owner reviews them and merges with the ruleset's admin bypass for PRs; outside contributions need the approval. Agents never approve, never merge with `--admin`, and merge only what the owner has said to merge. `.claude/settings.json` also denies direct and force pushes to `main` for agents.
- Commit messages are English, imperative mood, and end with the co-author line the environment provides.
