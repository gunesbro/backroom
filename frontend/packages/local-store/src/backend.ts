export interface Header {
  readonly key: string;
  readonly cell: string;
  /** Conversation id or key-value key; empty for the cell record. */
  readonly name: string;
  readonly kind: 'conv' | 'meta' | 'kv';
  readonly bytes: number;
  readonly accessedAt: number;
}

export interface Put {
  readonly header: Header;
  /** Absent: only the header is updated (an access). */
  readonly value?: unknown;
}

/** Storage behind the cache logic; a SQLite implementation can follow the same shape. */
export interface Backend {
  readonly kind: 'indexeddb' | 'memory';
  readHeaders(): Promise<Header[]>;
  read(key: string): Promise<unknown>;
  /** Atomic. */
  write(puts: readonly Put[], deletes: readonly string[]): Promise<void>;
  clear(): Promise<void>;
  close(): void;
}

export function createMemoryBackend(): Backend {
  const headers = new Map<string, Header>();
  const values = new Map<string, unknown>();
  return {
    kind: 'memory',
    readHeaders: () => Promise.resolve([...headers.values()]),
    read: (key) => Promise.resolve(structuredClone(values.get(key))),
    write(puts, deletes) {
      for (const key of deletes) {
        headers.delete(key);
        values.delete(key);
      }
      for (const put of puts) {
        headers.set(put.header.key, put.header);
        if ('value' in put) values.set(put.header.key, structuredClone(put.value));
      }
      return Promise.resolve();
    },
    clear() {
      headers.clear();
      values.clear();
      return Promise.resolve();
    },
    close() {},
  };
}
