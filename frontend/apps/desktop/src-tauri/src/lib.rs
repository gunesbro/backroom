mod auth;

use tauri::{AppHandle, Manager, Runtime, WebviewWindowBuilder};
use tauri_plugin_deep_link::DeepLinkExt;

/// Starts the desktop shell. The UI is the web app (`apps/web`); this crate only hosts it and signs in.
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // First, so a second start only brings this window forward: two processes must never rotate the same
        // stored refresh token (contract, "One refresh at a time").
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            bring_to_front(app);
        }))
        .plugin(tauri_plugin_deep_link::init())
        // Used from Rust only, to open the sign-in page in the system browser; the web view gets no permission.
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![
            auth::auth_status,
            auth::sign_in,
            auth::reopen_sign_in,
            auth::sign_in_link,
            auth::cancel_sign_in,
            auth::access_token,
            auth::forget_session,
            auth::sign_out,
        ])
        .setup(|app| {
            // The URL scheme only starts the app or brings it forward; its address and parameters are ignored,
            // because any web page can call it (contract, flow 1, step 4). On Windows and Linux the call arrives as
            // a second start (the single-instance callback above), on macOS as this event.
            let handle = app.handle().clone();
            app.deep_link()
                .on_open_url(move |_| bring_to_front(&handle));
            register_url_scheme(app);

            let auth = auth::Auth::new(&app.config().identifier)?;
            app.manage(auth);
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move { handle.state::<auth::Auth>().resume().await });

            // The window is declared in tauri.conf.json with "create": false and built here,
            // so media permission requests and navigation can be controlled.
            let config = app
                .config()
                .app
                .windows
                .first()
                .cloned()
                .ok_or("tauri.conf.json declares no window")?;

            let window = WebviewWindowBuilder::from_config(app.handle(), &config)?
                // The main window only ever shows the app itself; links to other sites never load inside it.
                .on_navigation(origin::is_app)
                .on_permission_request(media::answer)
                .build()?;

            #[cfg(target_os = "linux")]
            linux::enable_media(&window);
            #[cfg(not(target_os = "linux"))]
            let _ = window;

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running the Tauri application");
}

/// Installers register the URL scheme. An AppImage has no installer, so it registers itself (per user). A
/// development build changes this per-user system setting only on request (README, "Frontend"). A failure only
/// means the browser cannot offer to open the app, so it never stops the app.
#[cfg(any(windows, target_os = "linux"))]
fn register_url_scheme<R: Runtime>(app: &tauri::App<R>) {
    #[cfg(target_os = "linux")]
    let needed = app.env().appimage.is_some();
    #[cfg(not(target_os = "linux"))]
    let needed = false;

    if (needed
        || (cfg!(debug_assertions) && std::env::var_os("BACKROOM_REGISTER_URL_SCHEME").is_some()))
        && let Err(error) = app.deep_link().register_all()
    {
        eprintln!("could not register the URL scheme: {error}");
    }
}

#[cfg(not(any(windows, target_os = "linux")))]
fn register_url_scheme<R: Runtime>(_: &tauri::App<R>) {}

fn bring_to_front<R: Runtime>(app: &AppHandle<R>) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
}

mod origin {
    use tauri::Url;

    /// The app's own origins: bundled assets in release builds (`tauri://localhost` on macOS and Linux,
    /// `http(s)://tauri.localhost` on Windows, never with a port), and the web dev server in debug builds.
    pub fn is_app(url: &Url) -> bool {
        match (url.scheme(), url.host_str()) {
            ("tauri", Some("localhost")) => !cfg!(windows) && url.port().is_none(),
            ("http" | "https", Some("tauri.localhost")) => cfg!(windows) && url.port().is_none(),
            ("http", Some("localhost")) => cfg!(debug_assertions) && url.port() == Some(5173),
            _ => false,
        }
    }
}

mod media {
    use tauri::Runtime;
    use tauri::webview::{PermissionKind, PermissionResponse, Webview};

    /// Phase 0 spike: camera and microphone are granted without asking, but only in debug builds or
    /// builds with the `spike` feature, and only for the app's own origin. Product builds fall back to
    /// the web view's default behaviour until a consent flow exists.
    ///
    /// Screen capture is chosen by the user in the system picker. Only WebKitGTK (Linux) asks the app
    /// before showing the XDG portal picker, so only there is that request passed through.
    pub fn answer<R: Runtime>(webview: Webview<R>, kind: PermissionKind) -> PermissionResponse {
        let is_granted_kind = matches!(kind, PermissionKind::Camera | PermissionKind::Microphone)
            || (cfg!(target_os = "linux") && matches!(kind, PermissionKind::DisplayCapture));

        let from_app = webview.url().is_ok_and(|url| super::origin::is_app(&url));

        if cfg!(any(debug_assertions, feature = "spike")) && is_granted_kind && from_app {
            PermissionResponse::Allow
        } else {
            PermissionResponse::Default
        }
    }
}

#[cfg(target_os = "linux")]
mod linux {
    use webkit2gtk::{SettingsExt, WebViewExt};

    /// WebKitGTK keeps media capture and WebRTC switched off unless the embedder enables them.
    pub fn enable_media(window: &tauri::WebviewWindow) {
        let result = window.with_webview(|webview| {
            let view = webview.inner();
            if let Some(settings) = WebViewExt::settings(&view) {
                settings.set_enable_media_stream(true);
                settings.set_enable_mediasource(true);
                settings.set_enable_webrtc(true);
            }
        });

        if let Err(error) = result {
            eprintln!("could not enable WebKitGTK media settings: {error}");
        }
    }
}
