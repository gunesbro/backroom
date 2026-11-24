import type { Backend, Header, Put } from './backend.ts';

const HEADERS = 'headers';
const RECORDS = 'records';

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => {
      resolve(req.result);
    };
    req.onerror = () => {
      reject(req.error ?? new Error('indexeddb request failed'));
    };
  });
}

function openOnce(factory: IDBFactory, name: string, version: number): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = factory.open(name, version);
    // Migration path: any other schema version is wiped and rebuilt.
    req.onupgradeneeded = () => {
      const db = req.result;
      for (const existing of Array.from(db.objectStoreNames)) db.deleteObjectStore(existing);
      db.createObjectStore(HEADERS, { keyPath: 'key' });
      db.createObjectStore(RECORDS);
    };
    req.onsuccess = () => {
      resolve(req.result);
    };
    req.onerror = () => {
      reject(req.error ?? new Error('indexeddb open failed'));
    };
    req.onblocked = () => {
      reject(new Error('indexeddb open blocked'));
    };
  });
}

function deleteDatabase(factory: IDBFactory, name: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const req = factory.deleteDatabase(name);
    req.onsuccess = () => {
      resolve();
    };
    req.onerror = () => {
      reject(req.error ?? new Error('indexeddb delete failed'));
    };
    req.onblocked = () => {
      reject(new Error('indexeddb delete blocked'));
    };
  });
}

export async function openIndexedDbBackend(
  factory: IDBFactory,
  name: string,
  version: number,
): Promise<Backend> {
  let db: IDBDatabase;
  try {
    db = await openOnce(factory, name, version);
  } catch (error) {
    // The stored version is newer than ours (VersionError): unknown format, wipe it.
    if (error instanceof DOMException && error.name === 'VersionError') {
      await deleteDatabase(factory, name);
      db = await openOnce(factory, name, version);
    } else {
      throw error;
    }
  }
  db.onversionchange = () => {
    db.close();
  };

  function transaction(work: (tx: IDBTransaction) => void): Promise<void> {
    return new Promise((resolve, reject) => {
      const tx = db.transaction([HEADERS, RECORDS], 'readwrite');
      tx.oncomplete = () => {
        resolve();
      };
      tx.onerror = () => {
        reject(tx.error ?? new Error('indexeddb transaction failed'));
      };
      tx.onabort = () => {
        reject(tx.error ?? new Error('indexeddb transaction aborted'));
      };
      work(tx);
    });
  }

  return {
    kind: 'indexeddb',
    async readHeaders() {
      return (await request(db.transaction(HEADERS, 'readonly').objectStore(HEADERS).getAll())) as Header[];
    },
    read: (key) =>
      request(db.transaction(RECORDS, 'readonly').objectStore(RECORDS).get(key)) as Promise<unknown>,
    write(puts: readonly Put[], deletes: readonly string[]) {
      return transaction((tx) => {
        const headers = tx.objectStore(HEADERS);
        const records = tx.objectStore(RECORDS);
        for (const key of deletes) {
          headers.delete(key);
          records.delete(key);
        }
        for (const put of puts) {
          headers.put(put.header);
          if ('value' in put) records.put(put.value, put.header.key);
        }
      });
    },
    clear() {
      return transaction((tx) => {
        tx.objectStore(HEADERS).clear();
        tx.objectStore(RECORDS).clear();
      });
    },
    close() {
      db.close();
    },
  };
}
