//! The rule for the build-time issuer, shared by build.rs (which refuses a release build that breaks it) and
//! the app's tests.

/// A release issuer: `https://<host>/realms/<name>` (an optional path prefix before `/realms` is allowed), with
/// a host name that is neither an IP address nor `localhost`, no port other than the default or an explicit
/// one, no trailing slash, query, fragment or user info. The app compares `iss` with it exactly and appends
/// endpoint paths to it, so its form matters.
pub fn is_release_issuer(issuer: &str) -> bool {
    let Ok(url) = url::Url::parse(issuer) else {
        return false;
    };
    let host_is_name = matches!(url.host(), Some(url::Host::Domain(host))
        if !host.eq_ignore_ascii_case("localhost")
            && !host.to_ascii_lowercase().ends_with(".localhost")
            && !host.ends_with('.'));
    let path = url.path();
    let realm = path.rsplit_once("/realms/").map(|(_, realm)| realm);
    url.scheme() == "https"
        && host_is_name
        && url.username().is_empty()
        && url.password().is_none()
        && url.query().is_none()
        && url.fragment().is_none()
        && url.as_str() == issuer
        && realm.is_some_and(|realm| {
            !realm.is_empty()
                && realm
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_issuers_name_a_realm_on_an_https_host() {
        assert!(is_release_issuer("https://id.example.test/realms/backroom"));
        assert!(is_release_issuer(
            "https://example.test/auth/realms/backroom"
        ));
        assert!(is_release_issuer(
            "https://id.example.test:8443/realms/backroom"
        ));

        for issuer in [
            "",
            "http://id.example.test/realms/backroom",
            "https://localhost/realms/backroom",
            "https://LOCALHOST/realms/backroom",
            "https://app.localhost/realms/backroom",
            "https://id.example.test./realms/backroom",
            "https://127.0.0.1/realms/backroom",
            "https://127.1/realms/backroom",
            "https://0.0.0.0/realms/backroom",
            "https://[::1]/realms/backroom",
            "https://id.example.test/realms/backroom/",
            "https://id.example.test/realms/backroom?x=1",
            "https://id.example.test/realms/backroom#x",
            "https://user@id.example.test/realms/backroom",
            "https://id.example.test/realms/",
            "https://id.example.test/backroom",
            "https://ID.example.test/realms/backroom",
        ] {
            assert!(!is_release_issuer(issuer), "{issuer:?} must be refused");
        }
    }
}
