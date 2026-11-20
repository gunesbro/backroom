# Spike: Tauri with LiveKit (Phase 0 gate)

- Status: Windows passed; Linux device test deferred by the owner
- Started: 2026-10-25
- Windows tested: 2026-10-25
- Decision it informs: [ADR 0007](../adr/0007-tauri-for-desktop.md)

## Question

Can the Tauri v2 desktop app (system web view) join a LiveKit room with camera, microphone and screen sharing on Windows and Linux? macOS is built in CI only; a real-device test happens before the beta.

**Gate:** camera, microphone and screen sharing work on Windows and Linux. If not, the desktop technology is reconsidered before Phase 1. If only macOS fails, macOS users use the web app until it is fixed.

## Setup

- Spike screen: `frontend/apps/web/src/features/livekit-spike`, opened at `#/spike/livekit`. It shows the web view's capabilities (`RTCPeerConnection`, `getUserMedia`, `getDisplayMedia`, secure context, user agent), connects with a server URL and token, toggles microphone, camera and screen share, renders local and remote tracks and keeps a log. Available in development and in builds made with `VITE_ENABLE_SPIKES=true`.
- Desktop shell (`frontend/apps/desktop/src-tauri/src/lib.rs`): the main window is built in code so `on_permission_request` grants camera, microphone and display capture, but only in debug builds or builds with the `spike` Cargo feature, and only for the app's own origin. On Windows and macOS screen sharing goes through the system picker. On Linux, WebKitGTK's `enable-media-stream`, `enable-mediasource` and `enable-webrtc` settings are switched on, and the AppImage bundles the GStreamer media framework. A product build will ask the user instead of granting automatically.
- macOS: `Info.plist` carries the camera and microphone usage descriptions.
- CSP: allows the local LiveKit server (`ws://localhost:7880`) and LiveKit Cloud (`wss://*.livekit.cloud`), `blob:`/`mediastream:` media and `blob:` workers.
- Tokens: `tools/dev/New-LiveKitToken.ps1` creates them with the LiveKit CLI container; the local secret comes from the AppHost's user secrets, the cloud secret from `LIVEKIT_API_SECRET`. Nothing is stored in the repository.

## Test procedure

### Windows (real device, local LiveKit)

1. `dotnet run --project backend/src/Aspire/Backroom.AppHost` (starts LiveKit on `ws://localhost:7880`).
2. Two tokens: `./tools/dev/New-LiveKitToken.ps1 -Identity windows-desktop` and `-Identity windows-browser`.
3. Desktop: `pnpm -C frontend desktop:dev`, open the "LiveKit spike" link, connect with the first token.
4. Second participant: a browser at `http://localhost:5173/#/spike/livekit`, connect with the second token.
5. Use headphones, or mute the browser tab, to avoid feedback on the same machine. In the desktop app, turn on the microphone, camera and screen share; confirm the browser receives all three, and that the desktop app shows the browser's tracks.
6. Record the capabilities block (including codecs), the connect time from the log, and the app's memory (Task Manager: the app plus its WebView2 processes) with and without a call.
7. Repeat the call with the release installer from the Desktop packages workflow run with spikes enabled (it loads the app from `http://tauri.localhost`), and take the memory reading there.

### Linux (same computer, Ubuntu live USB, LiveKit Cloud)

1. Before rebooting: create a free LiveKit Cloud project, then two tokens valid for 48 hours with the cloud key (`-ApiKey <key> -ValidFor 48h`, with `LIVEKIT_API_SECRET` set). Download the Linux packages from the Desktop packages workflow run with spikes enabled.
2. Boot Ubuntu 24.04 or newer from USB. Enable the `universe` repository, then `sudo apt update` and upgrade WebKitGTK (`sudo apt install --only-upgrade libwebkit2gtk-4.1-0`), because the ISO ships an older build.
3. Install the GStreamer WebRTC stack that WebKitGTK uses: `sudo apt install gstreamer1.0-plugins-bad gstreamer1.0-plugins-good gstreamer1.0-nice gstreamer1.0-pipewire`. Check with `gst-inspect-1.0 webrtcbin nicesrc`.
4. Install the `.deb` (`sudo apt install ./backroom*.deb`) and also try the AppImage.
5. Second participant: the LiveKit Cloud project's web example or the browser spike screen with the second token.
6. Same checks as on Windows, including screen sharing on the default Wayland session (it goes through the XDG desktop portal and PipeWire).
7. Record the WebKitGTK version (`apt policy libwebkit2gtk-4.1-0`), the GStreamer version (`gst-inspect-1.0 --version`) and `$XDG_SESSION_TYPE`.
8. If `rtcPeerConnection` is false, check whether the distribution's WebKitGTK is built with WebRTC before declaring the gate failed: a build without it is a packaging question (a WebKitGTK with WebRTC in the AppImage, Flatpak), not necessarily a Tauri one. If `getDisplayMedia` is missing, note it separately.

### macOS

Build only (Desktop packages workflow with macOS selected). Record whether the build succeeds.

## Results

| Platform | Build | WebRTC available | Microphone | Camera | Screen share | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Windows (WebView2), dev | ✅ local | ✅ | ✅ sent and received | ✅ | ✅ system picker | Windows 11; camera: phone as a Windows "connected camera" |
| Windows (WebView2), release installer | ✅ CI | | | | | Not run yet (optional); installer is 1.2 MB (NSIS) / 1.7 MB (MSI) |
| Linux deb (WebKitGTK) | ✅ CI | | | | | Deferred by the owner (2026-10-25); WebKitGTK / GStreamer / session: |
| Linux AppImage | ✅ CI | | | | | Deferred |
| macOS | ✅ CI | – | – | – | – | CI build only (2026-10-25) |

Versions: Tauri 2.12.5 (crate) / 2.12.4 (CLI), wry 0.57.1, webkit2gtk crate 2.0.2, livekit-client 2.23.0, LiveKit server 1.13.11.

## Findings

### Windows (2026-10-25)

- Microphone, camera and screen share all work in the Tauri app (dev build, local LiveKit), with a browser as the second participant. Media was verified in both directions.
- Screen sharing goes through the WebView2 system picker; the app's permission handler is not involved there. Camera and microphone were granted by the spike's permission handler without a prompt.
- The first camera attempt failed with `NotReadableError: Could not start video source`. The machine has no physical camera; WebView2 picked the registered OBS virtual camera, which was not running. With the phone connected as a Windows "connected camera" the camera worked. Product consequences: device selection in meeting settings, and readable error messages for missing or busy devices.
- Local audio is not played back in the spike (to avoid echo). To hear yourself, listen on the other participant.
- Memory (working set, Task Manager shows only the small host process by default):

  | Situation | Host process | WebView2 processes | Total |
  | --- | --- | --- | --- |
  | Debug build with dev server, idle, no call | 36 MB | about 321 MB | about 357 MB |
  | Debug build with dev server, in a call with camera, microphone and screen share | 27 MB (4 MB private) | 540 MB in 7 processes (469 MB private) | about 568 MB |

  The budget (under 300 MB) applies outside meetings. The debug figures include the dev server client and HMR; an idle measurement of the release build is still needed and becomes part of the Phase 1 performance checks.

### Linux

- CI builds deb, rpm and AppImage on Ubuntu 24.04, including the WebKitGTK media and WebRTC settings and the bundled GStreamer media framework in the AppImage.
- The device test (Ubuntu live USB, LiveKit Cloud) was deferred by the owner on 2026-10-25. It is the remaining risk: if the distribution's WebKitGTK has no WebRTC, the desktop technology for Linux has to be reconsidered.

### macOS

- CI builds the DMG (1.5 MB) on macos-15. Real-device test before the beta.

### CI cost

- Desktop packages: first run Windows 7.9 min and Linux 6 min; with the Rust cache Windows 3.5 min, Linux 3.4 min, macOS 1.9 min (10x quota multiplier).

## Decision

- Windows: gate passed.
- Linux: pending the deferred device test. The owner decides whether Phase 1 starts before it.
- ADR 0007 records Tauri as accepted for Windows, with Linux pending.
