#[path = "src/auth/issuer.rs"]
mod issuer;

/// The app's own commands. Declaring them makes each one a permission, so only the capabilities that list it
/// (capabilities/default.json: the main window) can call it.
const COMMANDS: &[&str] = &[
    "auth_status",
    "sign_in",
    "reopen_sign_in",
    "sign_in_link",
    "cancel_sign_in",
    "access_token",
    "forget_session",
    "sign_out",
];

fn main() {
    // The sign-in issuer is fixed at build time (src/auth/config.rs), never taken from the web view.
    // Release builds must name a real https issuer, so a development default never ships.
    println!("cargo:rerun-if-env-changed=BACKROOM_AUTH_ISSUER");
    if std::env::var("PROFILE").as_deref() == Ok("release") {
        let issuer = std::env::var("BACKROOM_AUTH_ISSUER").unwrap_or_default();
        if !issuer::is_release_issuer(&issuer) {
            panic!(
                "release builds need BACKROOM_AUTH_ISSUER set to https://<host>/realms/<name> \
                 (no IP address, localhost, trailing slash, query or fragment)"
            );
        }
    }

    tauri_build::try_build(
        tauri_build::Attributes::new()
            .app_manifest(tauri_build::AppManifest::new().commands(COMMANDS)),
    )
    .expect("failed to run the Tauri build script");
}
