//! Where and as whom the app signs in. Fixed at build time: a compromised web view must not be able to
//! send the refresh token to another server.

/// The Keycloak realm (the token issuer). Release builds set `BACKROOM_AUTH_ISSUER` (checked in build.rs).
pub const ISSUER: &str = match option_env!("BACKROOM_AUTH_ISSUER") {
    Some(issuer) => issuer,
    None => "https://localhost:8443/realms/backroom",
};

/// The public client of the desktop app (backend realm file, client `desktop`).
pub const CLIENT_ID: &str = "desktop";

/// The only registered redirect path; Keycloak accepts any port on 127.0.0.1 (RFC 8252 §7.3).
pub const CALLBACK_PATH: &str = "/callback";

/// Scopes every sign-in requests besides the cells' (contract, "Audience per target").
pub const BASE_SCOPES: [&str; 2] = ["openid", "control-plane"];

/// The control plane's scope: a token target of its own.
pub const CONTROL_PLANE: &str = "control-plane";

/// At most 20 shared and 10 remembered cells (contract, "Limits").
pub const MAX_CELL_SCOPES: usize = 30;

pub fn authorization_endpoint() -> String {
    format!("{ISSUER}/protocol/openid-connect/auth")
}

pub fn token_endpoint() -> String {
    format!("{ISSUER}/protocol/openid-connect/token")
}

pub fn logout_endpoint() -> String {
    format!("{ISSUER}/protocol/openid-connect/logout")
}

/// A token target the web view may ask for: the control plane or `cell:{id}` with a valid cell id
/// (the backend's rule: lower-case letters, digits and hyphens, starting with a letter or digit, at most 63).
pub fn is_target(scope: &str) -> bool {
    scope == CONTROL_PLANE || scope.strip_prefix("cell:").is_some_and(is_cell_id)
}

pub fn is_cell_scope(scope: &str) -> bool {
    scope.strip_prefix("cell:").is_some_and(is_cell_id)
}

fn is_cell_id(id: &str) -> bool {
    let bytes = id.as_bytes();
    (1..=63).contains(&bytes.len())
        && bytes[0] != b'-'
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn targets_are_the_control_plane_or_valid_cell_scopes() {
        assert!(is_target("control-plane"));
        assert!(is_target("cell:eu-1"));
        assert!(is_target(&format!("cell:{}", "a".repeat(63))));

        for invalid in [
            "",
            "openid",
            "cell:",
            "cell:-eu",
            "cell:EU",
            "cell:eu 1",
            "cell:eu_1",
            "cell:eu-1 openid",
        ] {
            assert!(!is_target(invalid), "{invalid:?} must be refused");
        }
        assert!(!is_target(&format!("cell:{}", "a".repeat(64))));
    }

    #[test]
    fn endpoints_belong_to_the_issuer() {
        assert!(token_endpoint().starts_with(ISSUER));
        assert!(ISSUER.starts_with("https://"));
        assert!(!ISSUER.ends_with('/'));
    }
}
