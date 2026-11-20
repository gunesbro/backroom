//! The operating system's secure store (Windows Credential Manager, macOS Keychain, Secret Service).
//! Nothing here ever falls back to a file: without a store the app keeps the session in memory only.
//!
//! One Windows credential holds at most 2,560 bytes, so a value is split into entries of at most
//! [`CHUNK_BYTES`]. The first entry starts with a header: the number of entries, the value's length and the
//! first bytes of its SHA-256, so a write that stopped halfway is never read as a value.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const CHUNK_BYTES: usize = 2048;
const MAX_CHUNKS: usize = 8;
const HASH_BYTES: usize = 8;
const HEADER_BYTES: usize = 1 + 4 + HASH_BYTES;

#[derive(Debug, PartialEq, Eq)]
pub enum StoreError {
    /// There is no secure store on this system, or it refused access.
    Unavailable,
    /// A stored value is damaged (for example a write that stopped halfway).
    Damaged,
}

/// Named binary secrets of this app. Calls may block (an unlock prompt), so the session runs them on a
/// blocking thread.
pub trait SecretStore: Send + Sync {
    fn get(&self, name: &str) -> Result<Option<Vec<u8>>, StoreError>;
    fn set(&self, name: &str, value: &[u8]) -> Result<(), StoreError>;
    /// Deleting a missing entry succeeds.
    fn delete(&self, name: &str) -> Result<(), StoreError>;
}

/// The platform store, under the app's identifier as the service name.
pub struct KeyringStore {
    service: String,
}

impl KeyringStore {
    pub fn new(service: &str) -> Self {
        Self {
            service: service.to_owned(),
        }
    }

    fn entry(&self, name: &str) -> Result<keyring_core::Entry, StoreError> {
        // Sets up the platform store once; afterwards entries come from it.
        if keyring::Entry::store_status().is_err() {
            return Err(StoreError::Unavailable);
        }
        // Windows' default persistence roams with the user profile to other computers; a refresh token must
        // stay on this one (single-use, and it must not leave the device).
        #[cfg(windows)]
        let entry = keyring_core::Entry::new_with_modifiers(
            &self.service,
            name,
            &std::collections::HashMap::from([("persistence", "Local")]),
        );
        #[cfg(not(windows))]
        let entry = keyring_core::Entry::new(&self.service, name);
        entry.map_err(|_| StoreError::Unavailable)
    }
}

impl SecretStore for KeyringStore {
    fn get(&self, name: &str) -> Result<Option<Vec<u8>>, StoreError> {
        match self.entry(name)?.get_secret() {
            Ok(value) => Ok(Some(value)),
            Err(keyring_core::Error::NoEntry) => Ok(None),
            Err(_) => Err(StoreError::Unavailable),
        }
    }

    fn set(&self, name: &str, value: &[u8]) -> Result<(), StoreError> {
        self.entry(name)?
            .set_secret(value)
            .map_err(|_| StoreError::Unavailable)
    }

    fn delete(&self, name: &str) -> Result<(), StoreError> {
        match self.entry(name)?.delete_credential() {
            Ok(()) | Err(keyring_core::Error::NoEntry) => Ok(()),
            Err(_) => Err(StoreError::Unavailable),
        }
    }
}

/// What survives a restart: the refresh token, the scopes granted at sign-in and the account.
#[derive(Clone, Serialize, Deserialize)]
pub struct StoredSession {
    pub refresh_token: String,
    pub scopes: Vec<String>,
    #[serde(default)]
    pub user: Option<User>,
}

/// The signed-in account as the ID token describes it, for the app's screens.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct User {
    pub email: String,
    /// The full name; the email when the account has none.
    pub name: String,
    /// The first name for greetings; the full name when unknown.
    pub given_name: String,
}

pub const SESSION: &str = "session";

/// A refresh token whose logout call has not gone through yet (contract, flow 1, step 5). It is used for
/// nothing but that call.
pub const SIGN_OUT_PENDING: &str = "sign-out-pending";

pub fn read(store: &dyn SecretStore, key: &str) -> Result<Option<Vec<u8>>, StoreError> {
    let Some(first) = store.get(&chunk_name(key, 0))? else {
        return Ok(None);
    };
    if first.len() < HEADER_BYTES {
        return Err(StoreError::Damaged);
    }
    let count = usize::from(first[0]);
    let length = u32::from_le_bytes(first[1..5].try_into().expect("four bytes")) as usize;
    if !(1..=MAX_CHUNKS).contains(&count) || length > MAX_CHUNKS * CHUNK_BYTES {
        return Err(StoreError::Damaged);
    }

    let mut value = first[HEADER_BYTES..].to_vec();
    for index in 1..count {
        value.extend(
            store
                .get(&chunk_name(key, index))?
                .ok_or(StoreError::Damaged)?,
        );
    }
    if value.len() != length || hash(&value) != first[5..HEADER_BYTES] {
        return Err(StoreError::Damaged);
    }
    Ok(Some(value))
}

pub fn write(store: &dyn SecretStore, key: &str, value: &[u8]) -> Result<(), StoreError> {
    let first_capacity = CHUNK_BYTES - HEADER_BYTES;
    let (head, rest) = value.split_at(value.len().min(first_capacity));
    let tail: Vec<&[u8]> = rest.chunks(CHUNK_BYTES).collect();
    let count = 1 + tail.len();
    if count > MAX_CHUNKS {
        return Err(StoreError::Damaged);
    }

    for (index, chunk) in tail.iter().enumerate() {
        store.set(&chunk_name(key, index + 1), chunk)?;
    }
    let mut first = Vec::with_capacity(HEADER_BYTES + head.len());
    first.push(u8::try_from(count).expect("at most MAX_CHUNKS entries"));
    first.extend_from_slice(
        &u32::try_from(value.len())
            .expect("bounded by MAX_CHUNKS")
            .to_le_bytes(),
    );
    first.extend_from_slice(&hash(value));
    first.extend_from_slice(head);
    store.set(&chunk_name(key, 0), &first)?;

    for index in count..MAX_CHUNKS {
        store.delete(&chunk_name(key, index))?;
    }
    Ok(())
}

pub fn remove(store: &dyn SecretStore, key: &str) -> Result<(), StoreError> {
    for index in 0..MAX_CHUNKS {
        store.delete(&chunk_name(key, index))?;
    }
    Ok(())
}

fn hash(value: &[u8]) -> [u8; HASH_BYTES] {
    Sha256::digest(value)[..HASH_BYTES]
        .try_into()
        .expect("a SHA-256 is longer")
}

fn chunk_name(key: &str, index: usize) -> String {
    format!("{key}.{index}")
}

#[cfg(test)]
pub mod tests {
    use std::collections::HashMap;
    use std::sync::Mutex;

    use super::*;

    /// An in-memory store for tests; `unavailable` makes every call fail like a system without a store.
    #[derive(Default)]
    pub struct MemoryStore {
        pub entries: Mutex<HashMap<String, Vec<u8>>>,
        pub unavailable: bool,
    }

    impl SecretStore for MemoryStore {
        fn get(&self, name: &str) -> Result<Option<Vec<u8>>, StoreError> {
            if self.unavailable {
                return Err(StoreError::Unavailable);
            }
            Ok(self.entries.lock().unwrap().get(name).cloned())
        }

        fn set(&self, name: &str, value: &[u8]) -> Result<(), StoreError> {
            if self.unavailable {
                return Err(StoreError::Unavailable);
            }
            assert!(
                value.len() <= 2560,
                "a Windows credential holds at most 2,560 bytes"
            );
            self.entries
                .lock()
                .unwrap()
                .insert(name.to_owned(), value.to_vec());
            Ok(())
        }

        fn delete(&self, name: &str) -> Result<(), StoreError> {
            if self.unavailable {
                return Err(StoreError::Unavailable);
            }
            self.entries.lock().unwrap().remove(name);
            Ok(())
        }
    }

    fn value(size: usize, seed: u8) -> Vec<u8> {
        (0..size).map(|index| (index % 251) as u8 ^ seed).collect()
    }

    #[test]
    fn write_then_read_returns_the_value_for_any_size() {
        let store = MemoryStore::default();
        for size in [
            0,
            1,
            CHUNK_BYTES - HEADER_BYTES,
            CHUNK_BYTES,
            CHUNK_BYTES + 1,
            5 * CHUNK_BYTES + 7,
        ] {
            write(&store, SESSION, &value(size, 0)).unwrap();

            assert_eq!(
                read(&store, SESSION).unwrap(),
                Some(value(size, 0)),
                "size {size}"
            );
        }
    }

    #[test]
    fn write_of_a_shorter_value_removes_the_old_entries() {
        let store = MemoryStore::default();
        write(&store, SESSION, &value(3 * CHUNK_BYTES, 0)).unwrap();

        write(&store, SESSION, b"short").unwrap();

        assert_eq!(store.entries.lock().unwrap().len(), 1);
        assert_eq!(read(&store, SESSION).unwrap(), Some(b"short".to_vec()));
    }

    #[test]
    fn read_after_a_write_that_stopped_halfway_reports_damage() {
        // The new value's later entries were written, its first entry was not: the old header must not
        // accept the new entries.
        let store = MemoryStore::default();
        write(&store, SESSION, &value(2 * CHUNK_BYTES, 1)).unwrap();
        let old_first = store.entries.lock().unwrap()["session.0"].clone();
        write(&store, SESSION, &value(2 * CHUNK_BYTES, 2)).unwrap();
        store
            .entries
            .lock()
            .unwrap()
            .insert("session.0".into(), old_first);

        assert_eq!(read(&store, SESSION), Err(StoreError::Damaged));
    }

    #[test]
    fn read_of_a_missing_entry_reports_damage_and_remove_clears_it() {
        let store = MemoryStore::default();
        write(&store, SESSION, &value(2 * CHUNK_BYTES, 0)).unwrap();
        store.entries.lock().unwrap().remove("session.1");

        assert_eq!(read(&store, SESSION), Err(StoreError::Damaged));

        remove(&store, SESSION).unwrap();
        assert_eq!(read(&store, SESSION).unwrap(), None);
    }

    #[test]
    fn write_of_a_value_too_large_is_refused() {
        let store = MemoryStore::default();

        assert_eq!(
            write(&store, SESSION, &value(MAX_CHUNKS * CHUNK_BYTES, 0)),
            Err(StoreError::Damaged)
        );
    }

    /// Uses the real Credential Manager with a throwaway service name and removes its entry.
    #[cfg(windows)]
    #[test]
    fn keyring_store_on_windows_keeps_entries_on_this_computer() {
        let service = format!(
            "io.github.gunesbro.backroom.test-{}",
            super::super::pkce::random_value()
        );
        let store = KeyringStore::new(&service);

        store.set("probe.0", b"value").unwrap();
        let persistence =
            store.entry("probe.0").unwrap().get_attributes().unwrap()["persistence"].clone();
        store.delete("probe.0").unwrap();

        assert_eq!(persistence, "Local");
        assert_eq!(store.get("probe.0").unwrap(), None);
    }
}
