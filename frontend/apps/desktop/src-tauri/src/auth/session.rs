//! The signed-in session: the refresh token, the scopes granted at sign-in and short-lived access tokens
//! per target (contract, "Identity and tokens"). One lock serializes every token request, because each one
//! uses up the refresh token ("One refresh at a time").

use std::collections::HashMap;
use std::future::Future;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::Serialize;
use tokio::sync::Mutex;

use super::config::{MAX_CELL_SCOPES, is_target};
use super::store::{self, SESSION, SIGN_OUT_PENDING, SecretStore, StoreError, StoredSession, User};
use super::tokens::{Secret, TokenClient, TokenError, TokenResponse};

/// An access token is renewed when it has less than this left.
const RENEW_BEFORE: Duration = Duration::from_secs(30);

/// The control plane, the shared cells and the remembered ones, with room to spare.
const MAX_CACHED_TARGETS: usize = MAX_CELL_SCOPES + 2;

/// Keycloak issues 5-minute access tokens; a larger `expires_in` is not trusted.
const MAX_ACCESS_LIFETIME_SECS: u64 = 3600;

/// What the web view learns when a call fails; never a token or a server message.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum AuthError {
    /// No session (never signed in, signed out, or the session ended): sign in again.
    SignedOut,
    /// The target's scope was not granted at sign-in: sign in again with it.
    ScopeNotGranted,
    /// A remembered cell no longer exists (`invalid_scope`): sign in again without it.
    InvalidScope,
    /// The web view asked for something malformed.
    InvalidRequest,
    Cancelled,
    TimedOut,
    /// The person or Keycloak refused the sign-in.
    Denied,
    Network,
    Server,
    /// The loopback listener could not start.
    Listener,
}

/// Keycloak's token and logout endpoints (a trait so tests can stand in for Keycloak).
pub trait TokenEndpoint: Send + Sync + 'static {
    fn exchange_code(
        &self,
        code: &Secret,
        verifier: &str,
        redirect_uri: &str,
    ) -> impl Future<Output = Result<TokenResponse, TokenError>> + Send;
    fn refresh(
        &self,
        refresh_token: &Secret,
        scope: &str,
    ) -> impl Future<Output = Result<TokenResponse, TokenError>> + Send;
    fn logout(&self, refresh_token: &Secret)
    -> impl Future<Output = Result<(), TokenError>> + Send;
}

impl TokenEndpoint for TokenClient {
    fn exchange_code(
        &self,
        code: &Secret,
        verifier: &str,
        redirect_uri: &str,
    ) -> impl Future<Output = Result<TokenResponse, TokenError>> + Send {
        TokenClient::exchange_code(self, code, verifier, redirect_uri)
    }

    fn refresh(
        &self,
        refresh_token: &Secret,
        scope: &str,
    ) -> impl Future<Output = Result<TokenResponse, TokenError>> + Send {
        TokenClient::refresh(self, refresh_token, scope)
    }

    fn logout(
        &self,
        refresh_token: &Secret,
    ) -> impl Future<Output = Result<(), TokenError>> + Send {
        TokenClient::logout(self, refresh_token)
    }
}

pub struct Session<E: TokenEndpoint> {
    endpoint: E,
    store: Arc<dyn SecretStore>,
    state: Mutex<State>,
    /// Separate from `state`, so a logout retry waiting on the network never holds up token requests.
    pending: Mutex<()>,
}

#[derive(Default)]
struct State {
    loaded: bool,
    refresh_token: Option<Secret>,
    granted: Vec<String>,
    user: Option<User>,
    access: HashMap<String, (Secret, Instant)>,
}

impl State {
    fn clear(&mut self) {
        self.refresh_token = None;
        self.granted.clear();
        self.user = None;
        self.access.clear();
    }
}

impl<E: TokenEndpoint> Session<E> {
    pub fn new(endpoint: E, store: Arc<dyn SecretStore>) -> Self {
        Self {
            endpoint,
            store,
            state: Mutex::new(State::default()),
            pending: Mutex::new(()),
        }
    }

    /// Whether a session is stored; does not call Keycloak (the first token request does).
    #[cfg(test)]
    pub async fn is_signed_in(&self) -> bool {
        self.user().await.is_some()
    }

    /// The signed-in account, if a session is stored.
    pub async fn user(&self) -> Option<User> {
        let mut state = self.state.lock().await;
        self.load(&mut state).await;
        // A session without an account (stored before the account was kept) signs in again.
        state.refresh_token.as_ref().and(state.user.clone())
    }

    /// Finishes a sign-in with the authorization code. The access token of the code exchange carries every
    /// granted audience, so it is discarded unused (contract, "One audience per token"). A session it replaces
    /// is logged out unless it is the same Keycloak session (a sign-in that only added a cell's scope).
    pub async fn complete_sign_in(
        &self,
        code: &Secret,
        verifier: &str,
        redirect_uri: &str,
    ) -> Result<(), AuthError> {
        let mut state = self.state.lock().await;
        self.load(&mut state).await;
        let response = self
            .endpoint
            .exchange_code(code, verifier, redirect_uri)
            .await
            .map_err(map_error)?;
        let refresh_token = response.refresh_token.ok_or(AuthError::Server)?;

        let replaced = state.refresh_token.take();
        state.clear();
        state.granted = response
            .scope
            .split_whitespace()
            .filter(|scope| is_target(scope))
            .map(str::to_owned)
            .collect();
        state.refresh_token = Some(refresh_token.clone());
        state.user = response.id_token.as_ref().and_then(user_of);
        self.save(&state).await;
        drop(state);

        if let Some(replaced) = replaced
            && session_id(&replaced) != session_id(&refresh_token)
        {
            let _ = self.endpoint.logout(&replaced).await;
        }
        Ok(())
    }

    /// An access token for exactly one target (`control-plane` or `cell:{id}`).
    pub async fn access_token(&self, target: &str) -> Result<Secret, AuthError> {
        if !is_target(target) {
            return Err(AuthError::InvalidRequest);
        }

        let mut state = self.state.lock().await;
        self.load(&mut state).await;
        if let Some((token, valid_until)) = state.access.get(target)
            && Instant::now() + RENEW_BEFORE < *valid_until
        {
            return Ok(token.clone());
        }

        let refresh_token = state.refresh_token.clone().ok_or(AuthError::SignedOut)?;
        if !state.granted.iter().any(|scope| scope == target) {
            return Err(AuthError::ScopeNotGranted);
        }

        let response = match self.endpoint.refresh(&refresh_token, target).await {
            Ok(response) => response,
            Err(TokenError::InvalidGrant) => {
                // The session ended (signed out elsewhere, revoked, expired) or the token was used up.
                state.clear();
                self.blocking(|store| store::remove(store, SESSION))
                    .await
                    .ok();
                return Err(AuthError::SignedOut);
            }
            Err(error) => return Err(map_error(error)),
        };

        // The old refresh token is used up whatever the answer says, so keep the new one first.
        let Some(next) = response.refresh_token else {
            state.clear();
            self.blocking(|store| store::remove(store, SESSION))
                .await
                .ok();
            return Err(AuthError::SignedOut);
        };
        state.refresh_token = Some(next);
        self.save(&state).await;

        // Keycloak silently leaves out a scope it cannot grant.
        if !response
            .scope
            .split_whitespace()
            .any(|scope| scope == target)
        {
            return Err(AuthError::ScopeNotGranted);
        }

        if state.access.len() >= MAX_CACHED_TARGETS {
            let now = Instant::now();
            state
                .access
                .retain(|_, (_, valid_until)| *valid_until > now);
            if state.access.len() >= MAX_CACHED_TARGETS {
                state.access.clear();
            }
        }
        let lifetime = Duration::from_secs(response.expires_in.min(MAX_ACCESS_LIFETIME_SECS));
        state.access.insert(
            target.to_owned(),
            (response.access_token.clone(), Instant::now() + lifetime),
        );
        Ok(response.access_token)
    }

    /// Ends the session here and at Keycloak. If Keycloak cannot be reached, the token stays in the store only
    /// as "sign-out pending" and the logout is retried later (contract, flow 1, step 5).
    pub async fn sign_out(&self) {
        let _pending = self.pending.lock().await;
        let mut state = self.state.lock().await;
        self.load(&mut state).await;
        let refresh_token = state.refresh_token.take();
        state.clear();

        // Written and removed while `state` is held, so a sign-in finishing meanwhile cannot lose its session.
        let pending = match &refresh_token {
            Some(token) => {
                let bytes = token.expose().as_bytes().to_vec();
                self.blocking(move |store| store::write(store, SIGN_OUT_PENDING, &bytes))
                    .await
            }
            None => Ok(()),
        };
        self.blocking(|store| store::remove(store, SESSION))
            .await
            .ok();
        drop(state);

        let Some(refresh_token) = refresh_token else {
            return;
        };
        if self.logout(&refresh_token).await && pending.is_ok() {
            self.blocking(|store| store::remove(store, SIGN_OUT_PENDING))
                .await
                .ok();
        }
    }

    /// Drops the session on this device only, keeping the Keycloak session: after the server rejected a fresh
    /// token, a new sign-in then finishes through the browser's session without a password, and the person's
    /// other sessions stay signed in.
    pub async fn forget(&self) {
        let _pending = self.pending.lock().await;
        let mut state = self.state.lock().await;
        state.loaded = true;
        state.clear();
        self.blocking(|store| store::remove(store, SESSION))
            .await
            .ok();
    }

    /// Retries a logout that could not reach Keycloak before. Returns whether none is left pending: a new
    /// sign-in waits for this, or the late logout could end the session it shares with the new one.
    pub async fn retry_pending_sign_out(&self) -> bool {
        let _pending = self.pending.lock().await;
        let token = match self
            .blocking(|store| store::read(store, SIGN_OUT_PENDING))
            .await
        {
            Ok(Some(bytes)) => String::from_utf8(bytes).ok(),
            Ok(None) | Err(StoreError::Unavailable) => return true,
            Err(StoreError::Damaged) => None,
        };
        let done = match token {
            Some(token) => self.logout(&Secret::new(token)).await,
            None => true,
        };
        if done {
            self.blocking(|store| store::remove(store, SIGN_OUT_PENDING))
                .await
                .ok();
        }
        done
    }

    /// Whether the token no longer needs a logout call (it went through, or Keycloak refused the token).
    async fn logout(&self, refresh_token: &Secret) -> bool {
        !matches!(
            self.endpoint.logout(refresh_token).await,
            Err(TokenError::Network | TokenError::Server)
        )
    }

    async fn load(&self, state: &mut State) {
        if state.loaded {
            return;
        }
        state.loaded = true;
        match self.blocking(|store| store::read(store, SESSION)).await {
            Ok(Some(bytes)) => match serde_json::from_slice::<StoredSession>(&bytes) {
                Ok(stored) => {
                    state.refresh_token = Some(Secret::new(stored.refresh_token));
                    state.user = stored.user;
                    state.granted = stored
                        .scopes
                        .into_iter()
                        .filter(|scope| is_target(scope))
                        .collect();
                }
                Err(_) => {
                    self.blocking(|store| store::remove(store, SESSION))
                        .await
                        .ok();
                }
            },
            Err(StoreError::Damaged) => {
                self.blocking(|store| store::remove(store, SESSION))
                    .await
                    .ok();
            }
            // No store: the session lives in memory only.
            Ok(None) | Err(StoreError::Unavailable) => {}
        }
    }

    async fn save(&self, state: &State) {
        let Some(refresh_token) = &state.refresh_token else {
            return;
        };
        let stored = StoredSession {
            refresh_token: refresh_token.expose().to_owned(),
            scopes: state.granted.clone(),
            user: state.user.clone(),
        };
        if let Ok(bytes) = serde_json::to_vec(&stored) {
            // Without a store the session lives in memory only; it never goes to a file.
            self.blocking(move |store| store::write(store, SESSION, &bytes))
                .await
                .ok();
        }
    }

    /// Runs a store call on a blocking thread: the store may wait for an unlock prompt.
    async fn blocking<T: Send + 'static>(
        &self,
        call: impl FnOnce(&dyn SecretStore) -> Result<T, StoreError> + Send + 'static,
    ) -> Result<T, StoreError> {
        let store = self.store.clone();
        tokio::task::spawn_blocking(move || call(store.as_ref()))
            .await
            .unwrap_or(Err(StoreError::Unavailable))
    }
}

/// The account from an ID token. It came straight from the token endpoint over TLS, so its signature need
/// not be checked to show a name (OpenID Connect Core 3.1.3.7).
fn user_of(id_token: &Secret) -> Option<User> {
    let claims = claims(id_token)?;
    let text = |name: &str| {
        claims
            .get(name)
            .and_then(serde_json::Value::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty())
    };
    let email = text("email")?.to_owned();
    let name = text("name").unwrap_or(&email).to_owned();
    let given_name = text("given_name").unwrap_or(&name).to_owned();
    Some(User {
        email,
        name,
        given_name,
    })
}

fn claims(token: &Secret) -> Option<serde_json::Value> {
    use base64::Engine;
    let payload = token.expose().split('.').nth(1)?;
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(payload)
        .ok()?;
    serde_json::from_slice(&bytes).ok()
}

/// The Keycloak session (`sid`) a refresh token belongs to. Read without verifying the signature: it only
/// decides whether a replaced token needs its own logout.
fn session_id(token: &Secret) -> Option<String> {
    claims(token)?.get("sid")?.as_str().map(str::to_owned)
}

fn map_error(error: TokenError) -> AuthError {
    match error {
        TokenError::InvalidGrant => AuthError::SignedOut,
        TokenError::InvalidScope => AuthError::InvalidScope,
        TokenError::Rejected => AuthError::Denied,
        TokenError::Network => AuthError::Network,
        TokenError::Server => AuthError::Server,
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Mutex as SyncMutex;
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::super::store::tests::MemoryStore;
    use super::*;

    /// Keycloak's rules as far as the session needs them: refresh tokens rotate and are single-use, a refresh
    /// stays within the scopes granted at sign-in, and every access token names one target.
    #[derive(Default)]
    struct FakeKeycloak {
        granted: SyncMutex<Vec<String>>,
        current: SyncMutex<Option<String>>,
        issued: AtomicUsize,
        refreshes: AtomicUsize,
        logout_fails: SyncMutex<bool>,
        logged_out: SyncMutex<Vec<String>>,
        expires_in: u64,
    }

    impl FakeKeycloak {
        fn new(granted: &[&str]) -> Self {
            Self {
                granted: SyncMutex::new(granted.iter().map(|scope| (*scope).to_owned()).collect()),
                expires_in: 300,
                ..Self::default()
            }
        }

        fn next_refresh_token(&self) -> String {
            let token = format!("refresh-{}", self.issued.fetch_add(1, Ordering::SeqCst));
            *self.current.lock().unwrap() = Some(token.clone());
            token
        }

        fn answer(&self, access: String, scope: String) -> TokenResponse {
            TokenResponse {
                access_token: Secret::new(access),
                expires_in: self.expires_in,
                refresh_token: Some(Secret::new(self.next_refresh_token())),
                id_token: None,
                scope,
            }
        }
    }

    impl TokenEndpoint for FakeKeycloak {
        async fn exchange_code(
            &self,
            code: &Secret,
            verifier: &str,
            _: &str,
        ) -> Result<TokenResponse, TokenError> {
            if (code.expose(), verifier) != ("code", "verifier") {
                return Err(TokenError::Rejected);
            }
            let scope = format!("openid {}", self.granted.lock().unwrap().join(" "));
            let mut answer = self.answer("all-audiences".into(), scope);
            answer.id_token = Some(jwt(
                r#"{"email":"ada@example.test","name":"Ada Lovelace","given_name":"Ada"}"#,
            ));
            Ok(answer)
        }

        async fn refresh(
            &self,
            refresh_token: &Secret,
            scope: &str,
        ) -> Result<TokenResponse, TokenError> {
            self.refreshes.fetch_add(1, Ordering::SeqCst);
            if self.current.lock().unwrap().as_deref() != Some(refresh_token.expose()) {
                return Err(TokenError::InvalidGrant);
            }
            let granted = self
                .granted
                .lock()
                .unwrap()
                .iter()
                .any(|granted| granted == scope);
            let answered = if granted {
                scope.to_owned()
            } else {
                String::new()
            };
            Ok(self.answer(format!("access-for-{answered}"), answered))
        }

        async fn logout(&self, refresh_token: &Secret) -> Result<(), TokenError> {
            if *self.logout_fails.lock().unwrap() {
                return Err(TokenError::Network);
            }
            self.logged_out
                .lock()
                .unwrap()
                .push(refresh_token.expose().to_owned());
            *self.current.lock().unwrap() = None;
            Ok(())
        }
    }

    async fn signed_in(keycloak: FakeKeycloak) -> Session<FakeKeycloak> {
        let session = Session::new(keycloak, Arc::new(MemoryStore::default()));
        session
            .complete_sign_in(
                &Secret::new("code".into()),
                "verifier",
                "http://127.0.0.1:1/callback",
            )
            .await
            .unwrap();
        session
    }

    #[tokio::test]
    async fn access_token_for_a_granted_target_names_that_target_only_and_is_cached() {
        let session = signed_in(FakeKeycloak::new(&["control-plane", "cell:eu-1"])).await;

        let control_plane = session.access_token("control-plane").await.unwrap();
        let cell = session.access_token("cell:eu-1").await.unwrap();
        let again = session.access_token("control-plane").await.unwrap();

        assert_eq!(control_plane.expose(), "access-for-control-plane");
        assert_eq!(cell.expose(), "access-for-cell:eu-1");
        assert_eq!(again, control_plane);
        assert_eq!(
            session.endpoint.refreshes.load(Ordering::SeqCst),
            2,
            "the second control-plane token came from the cache"
        );
    }

    #[tokio::test]
    async fn access_token_for_a_target_not_granted_asks_for_a_new_sign_in() {
        let session = signed_in(FakeKeycloak::new(&["control-plane"])).await;

        assert_eq!(
            session.access_token("cell:customer-1").await,
            Err(AuthError::ScopeNotGranted)
        );
        assert_eq!(
            session.endpoint.refreshes.load(Ordering::SeqCst),
            0,
            "no refresh token is used up for it"
        );
    }

    #[tokio::test]
    async fn access_token_when_keycloak_leaves_out_the_scope_keeps_the_rotated_refresh_token() {
        let session = signed_in(FakeKeycloak::new(&["control-plane", "cell:gone"])).await;
        session
            .endpoint
            .granted
            .lock()
            .unwrap()
            .retain(|scope| scope != "cell:gone");

        assert_eq!(
            session.access_token("cell:gone").await,
            Err(AuthError::ScopeNotGranted)
        );
        assert!(
            session.access_token("control-plane").await.is_ok(),
            "the session still works"
        );
    }

    #[tokio::test]
    async fn access_token_with_malformed_targets_is_refused_without_calling_keycloak() {
        let session = signed_in(FakeKeycloak::new(&["control-plane"])).await;

        for target in ["openid", "cell:EU", "control-plane cell:eu-1", ""] {
            assert_eq!(
                session.access_token(target).await,
                Err(AuthError::InvalidRequest)
            );
        }
        assert_eq!(session.endpoint.refreshes.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn access_tokens_requested_together_never_reuse_a_refresh_token() {
        let mut keycloak = FakeKeycloak::new(&["control-plane", "cell:a", "cell:b", "cell:c"]);
        keycloak.expires_in = 1; // always renewed
        let session = std::sync::Arc::new(signed_in(keycloak).await);

        let tasks: Vec<_> = [
            "control-plane",
            "cell:a",
            "cell:b",
            "cell:c",
            "cell:a",
            "control-plane",
        ]
        .into_iter()
        .map(|target| {
            let session = session.clone();
            tokio::spawn(async move { session.access_token(target).await })
        })
        .collect();

        for task in tasks {
            assert!(
                task.await.unwrap().is_ok(),
                "a parallel request used up another's refresh token"
            );
        }
    }

    #[tokio::test]
    async fn access_token_after_the_session_ended_signs_out_and_clears_the_store() {
        let session = signed_in(FakeKeycloak::new(&["control-plane"])).await;
        *session.endpoint.current.lock().unwrap() = None; // revoked at Keycloak

        assert_eq!(
            session.access_token("control-plane").await,
            Err(AuthError::SignedOut)
        );
        assert!(!session.is_signed_in().await);
        assert_eq!(store::read(session.store.as_ref(), SESSION).unwrap(), None);
    }

    #[tokio::test]
    async fn session_after_a_restart_continues_from_the_store() {
        let store: Arc<dyn SecretStore> = Arc::new(MemoryStore::default());
        let first = Session::new(FakeKeycloak::new(&["control-plane"]), store.clone());
        first
            .complete_sign_in(&Secret::new("code".into()), "verifier", "x")
            .await
            .unwrap();
        first.access_token("control-plane").await.unwrap();
        let keycloak = std::mem::take(&mut *first.endpoint.current.lock().unwrap());

        let restarted_keycloak = FakeKeycloak::new(&["control-plane"]);
        *restarted_keycloak.current.lock().unwrap() = keycloak;
        let restarted = Session::new(restarted_keycloak, store);

        assert!(restarted.is_signed_in().await);
        assert!(restarted.access_token("control-plane").await.is_ok());
    }

    #[tokio::test]
    async fn session_without_a_secure_store_works_in_memory() {
        let session = Session::new(
            FakeKeycloak::new(&["control-plane"]),
            Arc::new(MemoryStore {
                unavailable: true,
                ..MemoryStore::default()
            }),
        );

        session
            .complete_sign_in(&Secret::new("code".into()), "verifier", "x")
            .await
            .unwrap();

        assert!(session.access_token("control-plane").await.is_ok());
    }

    #[tokio::test]
    async fn complete_sign_in_when_the_exchange_fails_keeps_the_stored_session() {
        let store: Arc<dyn SecretStore> = Arc::new(MemoryStore::default());
        let first = Session::new(FakeKeycloak::new(&["control-plane"]), store.clone());
        first
            .complete_sign_in(&Secret::new("code".into()), "verifier", "x")
            .await
            .unwrap();
        let restarted = Session::new(FakeKeycloak::new(&["control-plane"]), store);

        let failed = restarted
            .complete_sign_in(&Secret::new("code".into()), "wrong verifier", "x")
            .await;

        assert_eq!(failed, Err(AuthError::Denied));
        assert!(
            restarted.is_signed_in().await,
            "the stored session is still there"
        );
    }

    fn jwt(claims: &str) -> Secret {
        use base64::Engine;
        let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(claims.as_bytes());
        Secret::new(format!("header.{payload}.signature"))
    }

    #[tokio::test]
    async fn user_after_sign_in_comes_from_the_id_token_and_survives_a_restart() {
        let store: Arc<dyn SecretStore> = Arc::new(MemoryStore::default());
        let first = Session::new(FakeKeycloak::new(&["control-plane"]), store.clone());
        first
            .complete_sign_in(&Secret::new("code".into()), "verifier", "x")
            .await
            .unwrap();
        let restarted = Session::new(FakeKeycloak::new(&["control-plane"]), store);

        let expected = User {
            email: "ada@example.test".into(),
            name: "Ada Lovelace".into(),
            given_name: "Ada".into(),
        };
        assert_eq!(first.user().await, Some(expected.clone()));
        assert_eq!(restarted.user().await, Some(expected));
    }

    #[test]
    fn user_of_an_id_token_falls_back_to_the_email() {
        let user = user_of(&jwt(r#"{"email":"ada@example.test","name":"  "}"#)).unwrap();

        assert_eq!(
            (user.name.as_str(), user.given_name.as_str()),
            ("ada@example.test", "ada@example.test")
        );
        assert_eq!(user_of(&jwt(r#"{"name":"No Email"}"#)), None);
    }

    #[test]
    fn session_id_is_read_from_the_token_payload() {
        use base64::Engine;
        let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .encode(br#"{"sid":"abc","typ":"Refresh"}"#);

        assert_eq!(
            session_id(&Secret::new(format!("h.{payload}.s"))),
            Some("abc".to_owned())
        );
        assert_eq!(session_id(&Secret::new("not-a-jwt".into())), None);
    }

    #[tokio::test]
    async fn forget_drops_the_local_session_without_a_logout() {
        let session = signed_in(FakeKeycloak::new(&["control-plane"])).await;

        session.forget().await;

        assert!(!session.is_signed_in().await);
        assert!(session.endpoint.logged_out.lock().unwrap().is_empty());
        assert!(session.store.get("session.0").unwrap().is_none());
    }

    #[tokio::test]
    async fn sign_out_ends_the_keycloak_session_and_clears_the_store() {
        let session = signed_in(FakeKeycloak::new(&["control-plane"])).await;

        session.sign_out().await;

        assert_eq!(session.endpoint.logged_out.lock().unwrap().len(), 1);
        assert!(!session.is_signed_in().await);
        assert!(session.store.get("session.0").unwrap().is_none());
        assert!(session.store.get("sign-out-pending.0").unwrap().is_none());
    }

    #[tokio::test]
    async fn sign_out_while_offline_keeps_the_token_only_for_a_later_logout() {
        let session = signed_in(FakeKeycloak::new(&["control-plane"])).await;
        *session.endpoint.logout_fails.lock().unwrap() = true;

        session.sign_out().await;

        assert!(!session.is_signed_in().await);
        assert_eq!(
            session.access_token("control-plane").await,
            Err(AuthError::SignedOut),
            "the pending token is not usable"
        );
        assert!(session.store.get("sign-out-pending.0").unwrap().is_some());

        *session.endpoint.logout_fails.lock().unwrap() = false;
        session.retry_pending_sign_out().await;

        assert_eq!(session.endpoint.logged_out.lock().unwrap().len(), 1);
        assert!(session.store.get("sign-out-pending.0").unwrap().is_none());
    }
}
