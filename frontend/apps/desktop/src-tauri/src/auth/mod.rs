//! Desktop sign-in through the system browser (OAuth for native apps, RFC 8252; docs/contracts/phase-1.md,
//! flow 1, desktop). The refresh token never leaves this process: the web view asks for access tokens, one
//! target at a time, through the commands below.

mod config;
#[cfg(test)]
mod issuer;
mod loopback;
mod page;
mod pkce;
mod session;
mod store;
mod tokens;

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tauri::{AppHandle, Runtime, State};
use tauri_plugin_opener::OpenerExt;
use tokio::sync::oneshot;
use url::Url;

use config::{BASE_SCOPES, ISSUER, MAX_CELL_SCOPES, authorization_endpoint, is_cell_scope};
use loopback::{Answer, Listener, Received, respond};
use page::Locale;
pub use session::AuthError;
use session::Session;
use store::KeyringStore;
use tokens::{Secret, TokenClient};

/// The listener closes after this (contract, flow 1, step 2).
const ATTEMPT_LIFETIME: Duration = Duration::from_secs(10 * 60);

pub struct Auth {
    session: Session<TokenClient>,
    attempt: Mutex<Option<Attempt>>,
    next_attempt: AtomicU64,
}

/// The sign-in waiting in the browser. Dropping it (a new attempt, or cancel) ends the old one's wait.
struct Attempt {
    id: u64,
    url: String,
    _cancel: oneshot::Sender<()>,
}

impl Auth {
    /// `service` names the app's entries in the secure store (the app identifier).
    pub fn new(service: &str) -> Result<Self, reqwest::Error> {
        Ok(Self {
            session: Session::new(TokenClient::new()?, Arc::new(KeyringStore::new(service))),
            attempt: Mutex::new(None),
            next_attempt: AtomicU64::new(1),
        })
    }

    /// At start: a logout that could not reach Keycloak last time is sent again.
    pub async fn resume(&self) {
        let _ = self.session.retry_pending_sign_out().await;
    }

    async fn sign_in<R: Runtime>(
        &self,
        app: &AppHandle<R>,
        cells: &[String],
        locale: Locale,
    ) -> Result<(), AuthError> {
        let scopes = sign_in_scopes(cells)?;
        // A logout still pending would end the Keycloak session this sign-in may join.
        if !self.session.retry_pending_sign_out().await {
            return Err(AuthError::Network);
        }

        let listener = Listener::bind().await.map_err(|_| AuthError::Listener)?;
        let redirect_uri = listener.redirect_uri();
        let verifier = pkce::random_value();
        let state = pkce::random_value();
        let url = authorization_url(
            &redirect_uri,
            &state,
            &pkce::challenge(&verifier),
            &scopes,
            locale,
        );

        let (cancel, cancelled) = oneshot::channel();
        let id = self.next_attempt.fetch_add(1, Ordering::SeqCst);
        *self.attempt.lock().expect("attempt lock") = Some(Attempt {
            id,
            url: url.clone(),
            _cancel: cancel,
        });
        // If the browser does not open, the screen still offers to open it again or copy the link.
        let _ = app.opener().open_url(&url, None::<&str>);

        let result = tokio::select! {
            received = tokio::time::timeout(ATTEMPT_LIFETIME, listener.receive(&state, ISSUER)) => match received {
                Err(_) => Err(AuthError::TimedOut),
                Ok(Err(_)) => Err(AuthError::Listener),
                Ok(Ok((received, mut stream))) => {
                    let result = match received {
                        Received::Answer(Answer::Code(code)) => {
                            self.session.complete_sign_in(&Secret::new(code), &verifier, &redirect_uri).await
                        }
                        Received::Answer(Answer::Error(_)) | Received::WrongIssuer => Err(AuthError::Denied),
                    };
                    let body = if result.is_ok() { page::complete(locale) } else { page::failed(locale) };
                    respond(&mut stream, 200, &body).await;
                    result
                }
            },
            _ = cancelled => Err(AuthError::Cancelled),
        };

        let mut attempt = self.attempt.lock().expect("attempt lock");
        if attempt.as_ref().is_some_and(|current| current.id == id) {
            *attempt = None;
        }
        drop(attempt);

        // The app does not take the focus from the browser; the completion page says to go back to it.
        result
    }

    fn attempt_url(&self) -> Option<String> {
        self.attempt
            .lock()
            .expect("attempt lock")
            .as_ref()
            .map(|attempt| attempt.url.clone())
    }
}

/// `openid control-plane` and the cells' scopes (contract, "Sign-in scopes").
fn sign_in_scopes(cells: &[String]) -> Result<Vec<String>, AuthError> {
    if cells.len() > MAX_CELL_SCOPES {
        return Err(AuthError::InvalidRequest);
    }
    let mut scopes: Vec<String> = BASE_SCOPES
        .iter()
        .map(|scope| (*scope).to_owned())
        .collect();
    for cell in cells {
        let scope = format!("cell:{cell}");
        if !is_cell_scope(&scope) {
            return Err(AuthError::InvalidRequest);
        }
        if !scopes.contains(&scope) {
            scopes.push(scope);
        }
    }
    Ok(scopes)
}

fn authorization_url(
    redirect_uri: &str,
    state: &str,
    challenge: &str,
    scopes: &[String],
    locale: Locale,
) -> String {
    let scope = scopes.join(" ");
    Url::parse_with_params(
        &authorization_endpoint(),
        [
            ("client_id", config::CLIENT_ID),
            ("response_type", "code"),
            ("redirect_uri", redirect_uri),
            ("scope", scope.as_str()),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("ui_locales", locale.tag()),
        ],
    )
    .expect("the issuer is a valid URL")
    .into()
}

// Commands for the web view. They return access tokens (5 minutes, one audience) but never the refresh token.

/// The account of the stored session, or none; the first `access_token` call checks it with Keycloak.
#[tauri::command]
pub async fn auth_status(auth: State<'_, Auth>) -> Result<Option<store::User>, AuthError> {
    Ok(auth.session.user().await)
}

/// Signs in through the system browser and resolves once the app has its tokens. `cells` are the ids of the
/// shared cells and the remembered ones; `locale` picks the language of Keycloak's and the listener's pages.
#[tauri::command]
pub async fn sign_in(
    app: AppHandle,
    auth: State<'_, Auth>,
    cells: Vec<String>,
    locale: String,
) -> Result<(), AuthError> {
    auth.sign_in(&app, &cells, Locale::from_tag(&locale)).await
}

/// Opens the waiting sign-in in the browser again (same `state` and verifier).
#[tauri::command]
pub fn reopen_sign_in(app: AppHandle, auth: State<'_, Auth>) -> Result<(), AuthError> {
    let url = auth.attempt_url().ok_or(AuthError::Cancelled)?;
    app.opener()
        .open_url(&url, None::<&str>)
        .map_err(|_| AuthError::Listener)
}

/// The waiting sign-in's link, for "Copy the sign-in link".
#[tauri::command]
pub fn sign_in_link(auth: State<'_, Auth>) -> Option<String> {
    auth.attempt_url()
}

#[tauri::command]
pub fn cancel_sign_in(auth: State<'_, Auth>) {
    *auth.attempt.lock().expect("attempt lock") = None;
}

/// An access token for `control-plane` or `cell:{id}`.
#[tauri::command]
pub async fn access_token(auth: State<'_, Auth>, target: String) -> Result<String, AuthError> {
    Ok(auth
        .session
        .access_token(&target)
        .await?
        .expose()
        .to_owned())
}

/// Drops the session on this device only (the server rejected a fresh token); see `Session::forget`.
#[tauri::command]
pub async fn forget_session(auth: State<'_, Auth>) -> Result<(), AuthError> {
    auth.session.forget().await;
    Ok(())
}

#[tauri::command]
pub async fn sign_out(auth: State<'_, Auth>) -> Result<(), AuthError> {
    auth.session.sign_out().await;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sign_in_scopes_add_the_cells_once_and_refuse_bad_ids() {
        let cells = vec!["eu-1".to_owned(), "us-1".to_owned(), "eu-1".to_owned()];
        assert_eq!(
            sign_in_scopes(&cells).unwrap(),
            ["openid", "control-plane", "cell:eu-1", "cell:us-1"]
        );

        assert_eq!(
            sign_in_scopes(&["EU".to_owned()]),
            Err(AuthError::InvalidRequest)
        );
        assert_eq!(
            sign_in_scopes(&["eu-1 offline_access".to_owned()]),
            Err(AuthError::InvalidRequest)
        );
        assert_eq!(
            sign_in_scopes(&vec!["a".to_owned(); MAX_CELL_SCOPES + 1]),
            Err(AuthError::InvalidRequest)
        );
    }

    #[test]
    fn authorization_url_carries_pkce_state_and_the_loopback_redirect() {
        let scopes = sign_in_scopes(&["eu-1".to_owned()]).unwrap();

        let url = Url::parse(&authorization_url(
            "http://127.0.0.1:5000/callback",
            "st",
            "ch",
            &scopes,
            Locale::Turkish,
        ))
        .unwrap();
        let query: std::collections::HashMap<_, _> = url.query_pairs().into_owned().collect();

        assert!(url.as_str().starts_with(ISSUER));
        assert_eq!(query["client_id"], "desktop");
        assert_eq!(query["response_type"], "code");
        assert_eq!(query["redirect_uri"], "http://127.0.0.1:5000/callback");
        assert_eq!(query["scope"], "openid control-plane cell:eu-1");
        assert_eq!(query["state"], "st");
        assert_eq!(query["code_challenge"], "ch");
        assert_eq!(query["code_challenge_method"], "S256");
        assert_eq!(query["ui_locales"], "tr");
        assert!(url.as_str().len() < 3 * 1024);
    }
}
