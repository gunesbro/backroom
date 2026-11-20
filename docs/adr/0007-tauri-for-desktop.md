# 0007. Tauri for the desktop app

- Status: Accepted for Windows (spike passed 2026-10-25); Linux pending its deferred device test
- Date: 2026-10-25

## Spike outcome

See [docs/spikes/tauri.md](../spikes/tauri.md). On Windows, camera, microphone and screen sharing work in the Tauri app with LiveKit. Linux and macOS packages build in CI; the Linux device test was deferred by the owner and remains the open risk. This record becomes fully accepted when the Linux test passes, or is superseded if it fails.

## Context

The desktop app must open in under 1.5 seconds and use less than 300 MB of memory outside meetings, keep running in the system tray, and support camera, microphone and screen sharing on Windows, macOS and Linux. Tauri uses the operating system's web view, which is small and fast, but WebRTC support varies: on Linux, WebKitGTK may need a build with WebRTC enabled, and macOS has known screen sharing issues.

## Decision

- The desktop app uses Tauri v2 and does not have its own UI: it runs the web app.
- The decision is conditional on a Phase 0 spike: a small screen in `frontend/apps/desktop` that joins a LiveKit room with camera, microphone and screen sharing.
  - Windows: tested on the real device against a local LiveKit server.
  - Linux: the CI-built package is tested on the same computer with an Ubuntu live USB, against a free LiveKit Cloud project.
  - macOS: built in CI only; a real-device test happens before the beta.
- The result is written to `docs/spikes/tauri.md`.

## Consequences

- Gate: camera, microphone and screen sharing must work on Windows and Linux. If not, the desktop technology is reconsidered before Phase 1. If only macOS fails, macOS users use the web app until it is fixed.
- Desktop tests use tauri-driver on Windows and Linux plus manual device tests before releases.
- macOS CI builds are triggered manually only, to protect the free GitHub Actions minutes.
