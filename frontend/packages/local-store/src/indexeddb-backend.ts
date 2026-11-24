import type {
  Backend,
  CellMeta,
  ConvHeader,
  KvRecord,
  LruEntry,
  StoredMessage,
  Totals,
  Tx,
} from './backend.ts';

const STORES = ['convs', 'kv', 'messages', 'meta', 'state'] as const;
const TOTALS_KEY = 'totals';
/** How long an open or delete may wait for other tabs to release the database. */
const BLOCKED_TIMEOUT_MS = 3000;

function asError(value: unknown): Error {
  return value instanceof Error ? value : new Error('indexeddb transaction failed');
}

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

function walk(
  source: IDBObjectStore | IDBIndex,
  range: IDBKeyRange | undefined,
  visit: (value: unknown, cursor: IDBCursorWithValue) => boolean,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const req = source.openCursor(range);
    req.onerror = () => {
      reject(req.error ?? new Error('indexeddb cursor failed'));
    };
    req.onsuccess = () => {
      const cursor = req.result;
      if (cursor && visit(cursor.value as unknown, cursor)) cursor.continue();
      else resolve();
    };
  });
}

/** Keys are [cell, name] arrays; strings sort before arrays, so [cell, []] bounds every name. */
const cellRange = (cell: string) => IDBKeyRange.bound([cell], [cell, []]);
const messageRange = (cell: string, conv: string) =>
  IDBKeyRange.bound([cell, conv, -Infinity], [cell, conv, Infinity]);

function createTx(tx: IDBTransaction): Tx {
  const convs = tx.objectStore('convs');
  const kv = tx.objectStore('kv');
  const messages = tx.objectStore('messages');
  const meta = tx.objectStore('meta');
  const state = tx.objectStore('state');

  return {
    async getTotals() {
      return ((await request(state.get(TOTALS_KEY))) as Totals | undefined) ?? { bytes: 0, convs: 0 };
    },
    async setTotals(totals) {
      await request(state.put(totals, TOTALS_KEY));
    },
    async getConv(cell, conv) {
      return (await request(convs.get([cell, conv]))) as ConvHeader | undefined;
    },
    async putConv(header) {
      await request(convs.put(header));
    },
    async deleteConv(cell, conv) {
      await request(convs.delete([cell, conv]));
      await request(messages.delete(messageRange(cell, conv)));
    },
    listConvs: (cell) => request(convs.getAll(cellRange(cell))) as Promise<ConvHeader[]>,
    async getMessage(cell, conv, seq) {
      return (await request(messages.get([cell, conv, seq]))) as StoredMessage | undefined;
    },
    async putMessage(message) {
      await request(messages.put(message, [message.cell, message.conv, message.seq]));
    },
    listMessages: (cell, conv) =>
      request(messages.getAll(messageRange(cell, conv))) as Promise<StoredMessage[]>,
    async deleteOldest(cell, conv, count) {
      let remaining = count;
      let freed = 0;
      if (remaining <= 0) return 0;
      await walk(messages, messageRange(cell, conv), (value, cursor) => {
        freed += (value as StoredMessage).bytes;
        cursor.delete();
        remaining--;
        return remaining > 0;
      });
      return freed;
    },
    async getKv(cell, key) {
      return (await request(kv.get([cell, key]))) as KvRecord | undefined;
    },
    async putKv(record) {
      await request(kv.put(record));
    },
    async deleteKv(cell, key) {
      await request(kv.delete([cell, key]));
    },
    listKv: (cell) => request(kv.getAll(cellRange(cell))) as Promise<KvRecord[]>,
    async getMeta(cell) {
      return (await request(meta.get(cell))) as CellMeta | undefined;
    },
    async putMeta(value) {
      await request(meta.put(value));
    },
    async deleteMeta(cell) {
      await request(meta.delete(cell));
    },
    async lru(kind, limit) {
      const entries: LruEntry[] = [];
      const source = (kind === 'conv' ? convs : kv).index('accessedAt');
      await walk(source, undefined, (raw) => {
        const value = raw as ConvHeader | KvRecord;
        entries.push({
          cell: value.cell,
          id: 'conv' in value ? value.conv : value.key,
          bytes: value.bytes,
        });
        return entries.length < limit;
      });
      return entries;
    },
  };
}

function openOnce(factory: IDBFactory, name: string, version: number): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const req = factory.open(name, version);
    // Migration path: any other schema version is wiped and rebuilt.
    req.onupgradeneeded = () => {
      const db = req.result;
      for (const existing of Array.from(db.objectStoreNames)) db.deleteObjectStore(existing);
      db.createObjectStore('convs', { keyPath: ['cell', 'conv'] }).createIndex('accessedAt', 'accessedAt');
      db.createObjectStore('kv', { keyPath: ['cell', 'key'] }).createIndex('accessedAt', 'accessedAt');
      db.createObjectStore('messages');
      db.createObjectStore('meta', { keyPath: 'cell' });
      db.createObjectStore('state');
    };
    req.onsuccess = () => {
      if (settled) {
        req.result.close(); // we gave up waiting; do not hold the database
        return;
      }
      settled = true;
      resolve(req.result);
    };
    req.onerror = () => {
      settled = true;
      reject(req.error ?? new Error('indexeddb open failed'));
    };
    // Another tab holds an older connection and has not closed it yet; wait a little, then give up.
    req.onblocked = () => {
      setTimeout(() => {
        if (settled) return;
        settled = true;
        reject(new Error('indexeddb open blocked'));
      }, BLOCKED_TIMEOUT_MS);
    };
  });
}

export function deleteIndexedDb(factory: IDBFactory, name: string): Promise<void> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const req = factory.deleteDatabase(name);
    req.onsuccess = () => {
      settled = true;
      resolve();
    };
    req.onerror = () => {
      settled = true;
      reject(req.error ?? new Error('indexeddb delete failed'));
    };
    req.onblocked = () => {
      setTimeout(() => {
        if (settled) return;
        settled = true;
        reject(new Error('indexeddb delete blocked'));
      }, BLOCKED_TIMEOUT_MS);
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
      await deleteIndexedDb(factory, name);
      db = await openOnce(factory, name, version);
    } else {
      throw error;
    }
  }
  let closed = false;
  // Another tab upgrades or deletes the database: release it at once; the store reopens on the next call.
  db.onversionchange = () => {
    closed = true;
    db.close();
  };
  db.onclose = () => {
    closed = true;
  };

  return {
    kind: 'indexeddb',
    get closed() {
      return closed;
    },
    transact<T>(fn: (tx: Tx) => Promise<T>): Promise<T> {
      return new Promise<T>((resolve, reject) => {
        let tx: IDBTransaction;
        try {
          tx = db.transaction([...STORES], 'readwrite');
        } catch (error) {
          reject(asError(error));
          return;
        }
        let result: T | undefined;
        let failure: unknown;
        tx.oncomplete = () => {
          resolve(result as T);
        };
        tx.onabort = () => {
          reject(asError(failure ?? tx.error));
        };
        fn(createTx(tx)).then(
          (value) => {
            result = value;
          },
          (error: unknown) => {
            failure = error;
            try {
              tx.abort();
            } catch {
              // Already finished.
            }
          },
        );
      });
    },
    close() {
      closed = true;
      db.close();
    },
  };
}
