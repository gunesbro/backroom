//! Random values for one sign-in attempt: PKCE (RFC 7636, `S256`) and `state`.

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use sha2::{Digest, Sha256};

/// 32 random bytes from the operating system, base64url: 43 characters, as RFC 7636 recommends.
pub fn random_value() -> String {
    let mut bytes = [0_u8; 32];
    getrandom::fill(&mut bytes)
        .expect("the operating system's random number generator is unavailable");
    URL_SAFE_NO_PAD.encode(bytes)
}

/// The `S256` code challenge of a verifier.
pub fn challenge(verifier: &str) -> String {
    URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn challenge_matches_the_rfc_7636_example() {
        assert_eq!(
            challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
        );
    }

    #[test]
    fn random_values_are_long_and_differ() {
        let (first, second) = (random_value(), random_value());
        assert_eq!(first.len(), 43);
        assert_ne!(first, second);
    }
}
