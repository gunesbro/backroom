//! Requests to Keycloak's token and logout endpoints. Only this module and the secure store see tokens;
//! they never reach logs, and `Debug` prints them redacted.

use std::fmt;
use std::time::Duration;

use serde::Deserialize;

use super::config::{CLIENT_ID, logout_endpoint, token_endpoint};

/// A token or code. Its `Debug` output never shows the value.
#[derive(Clone, Deserialize, PartialEq, Eq)]
#[serde(transparent)]
pub struct Secret(String);

impl Secret {
    pub fn new(value: String) -> Self {
        Self(value)
    }

    pub fn expose(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("Secret([redacted])")
    }
}

#[derive(Debug, Deserialize)]
pub struct TokenResponse {
    pub access_token: Secret,
    pub expires_in: u64,
    pub refresh_token: Option<Secret>,
    /// Only on the code exchange; read for the account's name and email.
    pub id_token: Option<Secret>,
    /// The scopes of this access token (on the code exchange: everything granted).
    #[serde(default)]
    pub scope: String,
}

#[derive(Debug, PartialEq, Eq)]
pub enum TokenError {
    /// The refresh token was used up or its session ended.
    InvalidGrant,
    /// A requested scope does not exist.
    InvalidScope,
    /// Any other refusal.
    Rejected,
    Network,
    Server,
}

#[derive(Deserialize)]
struct ErrorResponse {
    error: String,
}

pub struct TokenClient {
    http: reqwest::Client,
}

impl TokenClient {
    pub fn new() -> reqwest::Result<Self> {
        // reqwest is built without a default crypto provider; another part of the app may have set one already.
        let _ = rustls::crypto::ring::default_provider().install_default();
        let http = reqwest::Client::builder()
            .https_only(true)
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(Duration::from_secs(10))
            .timeout(Duration::from_secs(15))
            .user_agent(concat!("backroom-desktop/", env!("CARGO_PKG_VERSION")))
            .build()?;
        Ok(Self { http })
    }

    pub async fn exchange_code(
        &self,
        code: &Secret,
        verifier: &str,
        redirect_uri: &str,
    ) -> Result<TokenResponse, TokenError> {
        self.token_request(&[
            ("grant_type", "authorization_code"),
            ("client_id", CLIENT_ID),
            ("code", code.expose()),
            ("code_verifier", verifier),
            ("redirect_uri", redirect_uri),
        ])
        .await
    }

    /// A new access token for one target; the refresh token is used up and the answer carries the next one.
    pub async fn refresh(
        &self,
        refresh_token: &Secret,
        scope: &str,
    ) -> Result<TokenResponse, TokenError> {
        self.token_request(&[
            ("grant_type", "refresh_token"),
            ("client_id", CLIENT_ID),
            ("refresh_token", refresh_token.expose()),
            ("scope", scope),
        ])
        .await
    }

    /// Ends the Keycloak session (and so every token of it, on every device of this session).
    pub async fn logout(&self, refresh_token: &Secret) -> Result<(), TokenError> {
        let response = self
            .http
            .post(logout_endpoint())
            .form(&[
                ("client_id", CLIENT_ID),
                ("refresh_token", refresh_token.expose()),
            ])
            .send()
            .await
            .map_err(|_| TokenError::Network)?;
        if response.status().is_success() {
            Ok(())
        } else {
            Err(error_of(response).await)
        }
    }

    async fn token_request(&self, form: &[(&str, &str)]) -> Result<TokenResponse, TokenError> {
        let response = self
            .http
            .post(token_endpoint())
            .form(form)
            .send()
            .await
            .map_err(|_| TokenError::Network)?;
        if response.status().is_success() {
            response
                .json::<TokenResponse>()
                .await
                .map_err(|_| TokenError::Server)
        } else {
            Err(error_of(response).await)
        }
    }
}

async fn error_of(response: reqwest::Response) -> TokenError {
    if response.status().is_server_error() {
        return TokenError::Server;
    }
    match response.json::<ErrorResponse>().await {
        Ok(body) if body.error == "invalid_grant" => TokenError::InvalidGrant,
        Ok(body) if body.error == "invalid_scope" => TokenError::InvalidScope,
        _ => TokenError::Rejected,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn secrets_are_redacted_in_debug_output() {
        let response: TokenResponse = serde_json::from_str(
            r#"{"access_token":"a.b.c","expires_in":300,"refresh_token":"r.s.t","scope":"openid control-plane"}"#,
        )
        .unwrap();

        let printed = format!("{response:?}");

        assert!(!printed.contains("a.b.c"));
        assert!(!printed.contains("r.s.t"));
        assert_eq!(response.access_token.expose(), "a.b.c");
    }
}
