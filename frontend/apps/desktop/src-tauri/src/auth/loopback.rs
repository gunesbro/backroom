//! The one-time loopback listener that receives the authorization response (contract, flow 1, desktop).
//!
//! It listens on 127.0.0.1 only, on a port the operating system picks. It reads nothing but the request line:
//! requests for another path or with another `state` get an error and the listener keeps waiting, so another
//! program on this computer cannot end the attempt by guessing the port.

use std::collections::HashMap;
use std::io;
use std::net::Ipv4Addr;
use std::time::Duration;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinSet;
use tokio::time::timeout;
use url::Url;

use super::config::CALLBACK_PATH;

/// A request line plus headers larger than this is not a browser's redirect.
const MAX_REQUEST_BYTES: usize = 8 * 1024;

/// A browser sends its redirect at once; a connection that sends nothing is closed after this.
const READ_TIMEOUT: Duration = Duration::from_secs(2);

/// Connections read at the same time; more are dropped until one finishes.
const MAX_OPEN_CONNECTIONS: usize = 8;

/// The longest authorization code we accept.
const MAX_CODE_LENGTH: usize = 4096;

pub struct Listener {
    inner: TcpListener,
    port: u16,
}

/// The authorization server's answer to this attempt (the request carried the expected `state` and `iss`).
#[derive(Debug, PartialEq, Eq)]
pub enum Answer {
    Code(String),
    /// An OAuth error code such as `access_denied`, or `invalid_response` when the answer had neither.
    Error(String),
}

/// What a single request to the listener was.
#[derive(Debug, PartialEq, Eq)]
enum Request {
    /// Another path or method, or not HTTP: answered with 404 and ignored.
    Other,
    /// The callback without the expected `state`: answered with 400 and ignored.
    WrongState,
    /// The expected `state` but another issuer (a mix-up, RFC 9207): ends the attempt.
    WrongIssuer,
    Answer(Answer),
}

/// How a request ended the attempt.
#[derive(Debug, PartialEq, Eq)]
pub enum Received {
    Answer(Answer),
    WrongIssuer,
}

impl Listener {
    pub async fn bind() -> io::Result<Self> {
        let inner = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await?;
        let port = inner.local_addr()?.port();
        Ok(Self { inner, port })
    }

    pub fn redirect_uri(&self) -> String {
        format!("http://127.0.0.1:{}{CALLBACK_PATH}", self.port)
    }

    /// Waits for the request that ends the attempt and returns it with its connection, which the caller answers.
    pub async fn receive(&self, state: &str, issuer: &str) -> io::Result<(Received, TcpStream)> {
        // Connections are read side by side, so one that sends nothing (a browser's preconnect, or a program
        // holding a socket open) cannot delay the real redirect.
        let mut reading = JoinSet::new();
        loop {
            tokio::select! {
                accepted = self.inner.accept() => match accepted {
                    Ok((stream, _)) if reading.len() < MAX_OPEN_CONNECTIONS => {
                        reading.spawn(async move {
                            let mut stream = stream;
                            let target = timeout(READ_TIMEOUT, read_target(&mut stream)).await.ok().and_then(Result::ok).flatten();
                            (target, stream)
                        });
                    }
                    // Too many at once: this one is dropped.
                    Ok(_) => {}
                    // For example a connection reset before it was accepted; the listener itself is fine.
                    Err(_) => tokio::time::sleep(Duration::from_millis(50)).await,
                },
                Some(Ok((target, stream))) = reading.join_next() => {
                    let Some(target) = target else {
                        answer_and_close(stream, 400);
                        continue;
                    };
                    match classify(&target, state, issuer) {
                        Request::Other => answer_and_close(stream, 404),
                        Request::WrongState => answer_and_close(stream, 400),
                        Request::WrongIssuer => return Ok((Received::WrongIssuer, stream)),
                        Request::Answer(answer) => return Ok((Received::Answer(answer), stream)),
                    }
                }
            }
        }
    }
}

/// Answers an ignored request without holding up the listener.
fn answer_and_close(mut stream: TcpStream, status: u16) {
    tokio::spawn(async move {
        let _ = timeout(READ_TIMEOUT, respond(&mut stream, status, "")).await;
    });
}

/// Answers a request with an HTML page that loads nothing else and is never cached, then closes the connection.
pub async fn respond(stream: &mut TcpStream, status: u16, body: &str) {
    let reason = match status {
        200 => "OK",
        404 => "Not Found",
        _ => "Bad Request",
    };
    let head = format!(
        "HTTP/1.1 {status} {reason}\r\n\
         Content-Type: text/html; charset=utf-8\r\n\
         Content-Length: {}\r\n\
         Cache-Control: no-store\r\n\
         Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n\
         X-Content-Type-Options: nosniff\r\n\
         Referrer-Policy: no-referrer\r\n\
         Connection: close\r\n\r\n",
        body.len()
    );
    // The browser may already have gone; nothing to do then.
    let _ = stream.write_all(head.as_bytes()).await;
    let _ = stream.write_all(body.as_bytes()).await;
    let _ = stream.shutdown().await;
}

/// The request target of a `GET`, read up to the end of the headers.
async fn read_target(stream: &mut TcpStream) -> io::Result<Option<String>> {
    let mut buffer = Vec::with_capacity(1024);
    let mut chunk = [0_u8; 1024];
    while !buffer.windows(4).any(|window| window == b"\r\n\r\n") {
        let read = stream.read(&mut chunk).await?;
        if read == 0 || buffer.len() + read > MAX_REQUEST_BYTES {
            return Ok(None);
        }
        buffer.extend_from_slice(&chunk[..read]);
    }

    let line = buffer
        .split(|byte| *byte == b'\r')
        .next()
        .unwrap_or_default();
    let line = std::str::from_utf8(line).unwrap_or_default();
    let mut parts = line.split(' ');
    Ok(match (parts.next(), parts.next(), parts.next()) {
        (Some("GET"), Some(target), Some(version)) if version.starts_with("HTTP/1.") => {
            Some(target.to_owned())
        }
        _ => None,
    })
}

fn classify(target: &str, state: &str, issuer: &str) -> Request {
    if !target.starts_with('/') {
        return Request::Other;
    }
    let Ok(url) = Url::parse(&format!("http://127.0.0.1{target}")) else {
        return Request::Other;
    };
    if url.path() != CALLBACK_PATH {
        return Request::Other;
    }

    let mut query: HashMap<String, String> = HashMap::new();
    for (key, value) in url.query_pairs() {
        // A repeated parameter makes the answer ambiguous.
        if query.insert(key.into_owned(), value.into_owned()).is_some() {
            return Request::WrongState;
        }
    }

    if query.get("state").map(String::as_str) != Some(state) {
        return Request::WrongState;
    }
    if query.get("iss").map(String::as_str) != Some(issuer) {
        return Request::WrongIssuer;
    }

    if let Some(error) = query.get("error") {
        let known = error.len() <= 64
            && error
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte == b'_');
        return Request::Answer(Answer::Error(if known {
            error.clone()
        } else {
            "invalid_response".into()
        }));
    }
    match query.get("code") {
        Some(code) if !code.is_empty() && code.len() <= MAX_CODE_LENGTH => {
            Request::Answer(Answer::Code(code.clone()))
        }
        _ => Request::Answer(Answer::Error("invalid_response".into())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ISSUER: &str = "https://id.example.test/realms/backroom";
    const ISS: &str = "https%3A%2F%2Fid.example.test%2Frealms%2Fbackroom";

    #[test]
    fn classify_with_the_expected_state_and_issuer_returns_the_code() {
        let target = format!("/callback?state=s1&code=c1&iss={ISS}");
        assert_eq!(
            classify(&target, "s1", ISSUER),
            Request::Answer(Answer::Code("c1".into()))
        );
    }

    #[test]
    fn classify_ignores_other_paths_and_wrong_states() {
        assert_eq!(classify("/favicon.ico", "s1", ISSUER), Request::Other);
        assert_eq!(
            classify("/callback/more?state=s1&code=c1", "s1", ISSUER),
            Request::Other
        );
        assert_eq!(
            classify("http://evil.test/callback?state=s1", "s1", ISSUER),
            Request::Other
        );
        assert_eq!(
            classify(
                &format!("/callback?state=s2&code=c1&iss={ISS}"),
                "s1",
                ISSUER
            ),
            Request::WrongState
        );
        assert_eq!(
            classify(&format!("/callback?code=c1&iss={ISS}"), "s1", ISSUER),
            Request::WrongState
        );
        assert_eq!(
            classify(
                &format!("/callback?state=s1&state=s1&code=c1&iss={ISS}"),
                "s1",
                ISSUER
            ),
            Request::WrongState
        );
    }

    #[test]
    fn classify_with_another_or_no_issuer_is_a_mix_up() {
        let other = "/callback?state=s1&code=c1&iss=https%3A%2F%2Fevil.test%2Frealms%2Fx";
        assert_eq!(classify(other, "s1", ISSUER), Request::WrongIssuer);
        assert_eq!(
            classify("/callback?state=s1&code=c1", "s1", ISSUER),
            Request::WrongIssuer
        );
    }

    #[test]
    fn classify_returns_errors_and_refuses_unknown_ones() {
        let denied = format!("/callback?state=s1&error=access_denied&iss={ISS}");
        assert_eq!(
            classify(&denied, "s1", ISSUER),
            Request::Answer(Answer::Error("access_denied".into()))
        );
        let odd = format!("/callback?state=s1&error=%3Cscript%3E&iss={ISS}");
        assert_eq!(
            classify(&odd, "s1", ISSUER),
            Request::Answer(Answer::Error("invalid_response".into()))
        );
        let empty = format!("/callback?state=s1&iss={ISS}");
        assert_eq!(
            classify(&empty, "s1", ISSUER),
            Request::Answer(Answer::Error("invalid_response".into()))
        );
    }

    #[tokio::test]
    async fn receive_keeps_waiting_after_wrong_requests_and_returns_the_answer() {
        let listener = Listener::bind().await.unwrap();
        let address = listener.redirect_uri();
        assert!(address.starts_with("http://127.0.0.1:"));
        let port = listener.port;

        let client = tokio::spawn(async move {
            let _silent = TcpStream::connect((Ipv4Addr::LOCALHOST, port))
                .await
                .unwrap();
            let wrong = send(
                port,
                "GET /callback?state=other&code=x HTTP/1.1\r\nHost: x\r\n\r\n",
            )
            .await;
            let other = send(port, "GET /favicon.ico HTTP/1.1\r\n\r\n").await;
            let garbage = send(port, "POST /callback HTTP/1.1\r\n\r\n").await;
            let right = format!("GET /callback?state=s1&code=c1&iss={ISS} HTTP/1.1\r\n\r\n");
            let mut stream = TcpStream::connect((Ipv4Addr::LOCALHOST, port))
                .await
                .unwrap();
            stream.write_all(right.as_bytes()).await.unwrap();
            let mut answer = String::new();
            stream.read_to_string(&mut answer).await.unwrap();
            (wrong, other, garbage, answer)
        });

        let (received, mut stream) = listener.receive("s1", ISSUER).await.unwrap();
        assert_eq!(received, Received::Answer(Answer::Code("c1".into())));
        respond(&mut stream, 200, "done").await;

        let (wrong, other, garbage, answer) = client.await.unwrap();
        assert!(wrong.starts_with("HTTP/1.1 400"));
        assert!(other.starts_with("HTTP/1.1 404"));
        assert!(garbage.starts_with("HTTP/1.1 400"));
        assert!(answer.starts_with("HTTP/1.1 200"));
        assert!(answer.contains("Cache-Control: no-store"));
        assert!(answer.ends_with("done"));
    }

    async fn send(port: u16, request: &str) -> String {
        let mut stream = TcpStream::connect((Ipv4Addr::LOCALHOST, port))
            .await
            .unwrap();
        stream.write_all(request.as_bytes()).await.unwrap();
        let mut answer = String::new();
        stream.read_to_string(&mut answer).await.unwrap();
        answer
    }
}
