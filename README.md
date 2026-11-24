# Backroom

Backroom: privacy-first team workspace. Team messaging, a virtual office and drop-in voice and video rooms in one app. Web and desktop (Tauri) first; mobile later. Code and commands still use the code name "Backroom" for now.

Backroom is source-available under PolyForm Noncommercial 1.0.0. It is not open source; see [License](#license).

- Requirements and architecture: [docs/requirements.md](docs/requirements.md)
- Contributing: [CONTRIBUTING.md](CONTRIBUTING.md) and the [CLA](CLA.md)
- Security policy: [SECURITY.md](SECURITY.md)
- Architecture decision records: [docs/adr/](docs/adr/)
- UI mockups: [docs/design/](docs/design/)
- Rules for AI agents working in this repo: [CLAUDE.md](CLAUDE.md)

## Status

Phase 0 (preparation): repository skeleton, local orchestration, CI and the Tauri desktop spike. See the roadmap in the requirements.

## Development setup (Windows)

The main development machine is Windows 11 with Docker Desktop on WSL 2. All backing services (PostgreSQL, Valkey, RabbitMQ, Keycloak, Mailpit, SeaweedFS, LiveKit) run as containers started by .NET Aspire; nothing else needs to be installed for them.

### Hardware

At least 16 GB RAM, with 6–8 GB available to Docker.

### Tools

Install with [winget](https://learn.microsoft.com/windows/package-manager/winget/). Some installers ask for administrator approval.

```powershell
winget install -e --id Git.Git
# C++ build tools and Windows SDK (needed by Rust/Tauri); install before Rust
winget install -e --id Microsoft.VisualStudio.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
winget install -e --id Rustlang.Rustup
winget install -e --id Microsoft.DotNet.SDK.10
winget install -e --id OpenJS.NodeJS.LTS
winget install -e --id pnpm.pnpm
winget install -e --id GitHub.cli
winget install -e --id Docker.DockerDesktop
```

Then:

1. Restart the terminal (or VS Code) so the new `PATH` is picked up.
2. Start Docker Desktop and make sure "Use the WSL 2 based engine" is enabled. Docker Desktop needs WSL 2; if it is missing, run `wsl --install` and restart.
3. Enable long paths (Rust `target` and pnpm folders can exceed 260 characters). In an elevated PowerShell:

   ```powershell
   New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -Value 1 -PropertyType DWord -Force
   ```

   Then, in a normal (non-elevated) terminal:

   ```powershell
   git config --global core.longpaths true
   ```

4. Sign in to GitHub: `gh auth login`.

Check the toolchain:

```powershell
git --version; gh --version; dotnet --version; node --version; pnpm --version; rustc --version; cargo --version; docker version
```

Expected: .NET SDK 10.x, the current Node.js LTS (24 or later), a running Docker engine on WSL 2.

### Run

With Docker Desktop running, from the repository root:

```powershell
dotnet run --project backend/src/Aspire/Backroom.AppHost
```

This starts every backing service as a container, the control plane and two shared cells, `a` and `b` (each with `app` and `worker` roles, its own database, RabbitMQ virtual host and Valkey key prefix), and opens the Aspire dashboard at http://localhost:15000 (the login link with its token is printed in the terminal). The first run downloads the container images and takes a few minutes. Press Ctrl+C to stop everything. PostgreSQL, Valkey, RabbitMQ, Keycloak and SeaweedFS keep their data in Docker volumes; Mailpit and LiveKit start empty each time.

A development certificate is recommended: `dotnet dev-certs https --trust` (Aspire may also create and trust one on first run). With it, Keycloak is served over HTTPS and the `https` launch profile works; the default launch profile is `http`.

| Service | Local address | Notes |
| --- | --- | --- |
| Aspire dashboard | http://localhost:15000 | Logs, traces, metrics and resource status |
| Control plane | http://localhost:5100 | `/health` in Development |
| Cell a (app role) | http://localhost:5200 | `/health` in Development |
| Cell a (worker role) | http://localhost:5201 | `/health` in Development |
| Cell b (app, worker roles) | http://localhost:5202, http://localhost:5203 | Second cell; `Backroom__Cells=1` runs only cell a (saves two host processes; see below) |
| Keycloak | https://localhost:8443 | Plain HTTP on the same port if no development certificate is trusted; admin credentials are in the dashboard (keycloak resource) |
| Mailpit | http://localhost:8025, SMTP `localhost:1025` | Catches all email; never relays |
| RabbitMQ | management UI linked from the dashboard | |
| PostgreSQL, Valkey | connection strings in the dashboard | |
| SeaweedFS (S3) | http://localhost:8333 | No credentials in development |
| LiveKit | ws://localhost:7880, RTC TCP 7881, UDP 7882 | API key `backroom-dev`; the secret is generated on first run |

Two cells roughly double the cell footprint (two .NET host pairs, six processes in all). On a small machine run one cell: `$env:Backroom__Cells = "1"` before `dotnet run`. The cells share one PostgreSQL server (roles are server-wide, so one set of role passwords), one Valkey (keys and channels are prefixed with the cell id) and one RabbitMQ (a virtual host per cell, created by the short-lived `rabbitmq-vhosts` container).

Each cell keeps its key and registration marker in `%APPDATA%\Backroom\dev\<apphost-id>\cells\<cell-id>`. `<apphost-id>` is the prefix of this checkout's PostgreSQL volume in `docker volume ls` (for `backroom.apphost-24f7985b54-postgres-data` it is `backroom.apphost-24f7985b54`), so every checkout and worktree has its own folders. A checkout that ran cells before this layout kept them in `%APPDATA%\Backroom\cells\<cell-id>`: copy each of those folders to the new place, or delete this checkout's Docker volumes so the cells register again. Otherwise the worker reports `control-plane-link` as unhealthy, because the control plane refuses to register a cell it already registered with the old key.

Passwords and the LiveKit secret are generated on first run and stored in the AppHost's user secrets (`dotnet user-secrets list --project backend/src/Aspire/Backroom.AppHost`). They never go into the repository.

PostgreSQL, RabbitMQ and Keycloak apply their password only when their volume is first created. If you reset the user secrets, delete this project's Docker volumes too (`docker volume ls`), or those services will reject the new passwords.

### Tests

```powershell
dotnet test --solution backend/Backroom.slnx   # unit, integration and architecture tests (integration tests need Docker)
```

The end-to-end tests (`backend/tests/Backroom.EndToEndTests`) start the real AppHost with Aspire testing: the control plane, both cells and their backing services, and run the Phase 1 flows against them as a client would (sign-in through the Keycloak page, organization creation, a message that reaches a second client over SignalR, catch-up, sign-out; nightly also the invitation through Mailpit). They need Docker, take a few minutes, and are skipped unless `BACKROOM_E2E=true`:

```powershell
$env:BACKROOM_E2E = "true"
dotnet test --project backend/tests/Backroom.EndToEndTests --filter-trait "Category=Short"   # the pull request set
dotnet test --project backend/tests/Backroom.EndToEndTests                                    # everything (nightly)
```

They run in isolation, so they can run while your own AppHost is running: fresh containers without volumes, random ports, no LiveKit or SeaweedFS (the tests do not use them, and LiveKit's ports are fixed), and the cells' keys in a temporary folder (`Backroom:Isolated`, `Backroom:KeyDirectoryRoot`). The test users' password is the run's `dev-user-password` parameter and is never printed. The **Backend** workflow runs the short set on every pull request that touches the backend (about 70 seconds locally with the images pulled); **End-to-end (nightly)** runs everything every night and on demand.

### Frontend

```powershell
pnpm -C frontend install
pnpm -C frontend --filter @backroom/web dev   # web app at http://localhost:5173
pnpm -C frontend desktop:dev                        # desktop app (Tauri) around the web app
```

The first desktop run compiles the Rust side and takes several minutes.

The browser's "open the app" question after signing in needs the app's URL scheme to be registered with the system. Installers do this; a development build runs without an installer, so to test that question on Windows or Linux, start it once with the scheme registered:

```powershell
$env:BACKROOM_REGISTER_URL_SCHEME = "1"; pnpm -C frontend desktop:dev
```

This is a per-user setting that points the scheme at the development build and needs no administrator rights. Remove it on Windows with `reg delete "HKCU\Software\Classes\backroom" /f`. On Linux it needs `xdg-mime` and `update-desktop-database`; remove `~/.local/share/applications/backroom-desktop-handler.desktop` and the scheme's line in `~/.config/mimeapps.list`. On macOS the scheme works only in an installed package. Everything else in the desktop app works without it.

### Desktop packages

Installers are built by the manually triggered **Desktop packages** workflow (GitHub → Actions → Desktop packages → Run workflow, or `gh workflow run desktop.yml -f windows=true -f linux=true -f macos=false`). It produces unsigned Windows (MSI, NSIS), Linux (deb, rpm, AppImage, built on Ubuntu 24.04) and, only when selected, macOS (DMG) packages as workflow artifacts kept for 14 days. macOS minutes count ten times against the free Actions quota.

## Secrets

Keys, passwords and tokens never go into the repository. Use .NET user-secrets or a git-ignored `.env` file. Outside production, all email is delivered to Mailpit.

Browser access: the control plane and each cell answer cross-origin requests only from the origins listed in `Cors:AllowedOrigins` (environment variable `Cors__AllowedOrigins__0`, `__1`, ...). Exact origins only; an empty list allows none. The AppHost sets the local web, ops and desktop origins.

## License

Backroom is source-available under the [PolyForm Noncommercial License 1.0.0](LICENSE) (required notice in [NOTICE](NOTICE)). This is not an OSI-approved open source license.

The license allows personal uses (research, experiment and testing for the benefit of public knowledge, personal study, private entertainment, hobby projects) and use by noncommercial organizations (charities, educational institutions, public research organizations, public safety or health organizations, environmental protection organizations and government institutions), regardless of funding source.

Anything else needs a separate commercial license from the owner, including a company using Backroom for its own team. To ask for one, open a GitHub Discussion in this repository or contact the maintainer through their GitHub profile.

Outside contributions require signing the [CLA](CLA.md); see [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).
