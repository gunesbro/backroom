/** Conversation header: counts and bytes live here so no scan of the messages is needed. */
export interface ConvHeader {
  readonly cell: string;
  readonly conv: string;
  readonly count: number;
  readonly bytes: number;
  readonly accessedAt: number;
}

export interface KvRecord {
  readonly cell: string;
  readonly key: string;
  readonly value: unknown;
  readonly bytes: number;
  readonly accessedAt: number;
}

/** One record per message, keyed by [cell, conversation, seq]. */
export interface StoredMessage {
  readonly cell: string;
  readonly conv: string;
  readonly seq: number;
  readonly bytes: number;
  readonly message: unknown;
}

export interface CellMeta {
  readonly cell: string;
  epoch?: string;
  feedCursor?: string;
  /** Insertion ordered: the oldest entries come first. */
  seqs: Record<string, number>;
}

export interface Totals {
  bytes: number;
  convs: number;
}

export interface LruEntry {
  readonly cell: string;
  readonly id: string;
  readonly bytes: number;
}

/** Everything the cache logic needs inside one transaction. */
export interface Tx {
  getTotals(): Promise<Totals>;
  setTotals(totals: Totals): Promise<void>;
  getConv(cell: string, conv: string): Promise<ConvHeader | undefined>;
  putConv(header: ConvHeader): Promise<void>;
  /** Deletes the header and every message of the conversation. */
  deleteConv(cell: string, conv: string): Promise<void>;
  listConvs(cell: string): Promise<ConvHeader[]>;
  getMessage(cell: string, conv: string, seq: number): Promise<StoredMessage | undefined>;
  putMessage(message: StoredMessage): Promise<void>;
  /** Ordered by seq. */
  listMessages(cell: string, conv: string): Promise<StoredMessage[]>;
  /** Deletes the lowest seqs and returns the bytes freed. */
  deleteOldest(cell: string, conv: string, count: number): Promise<number>;
  getKv(cell: string, key: string): Promise<KvRecord | undefined>;
  putKv(record: KvRecord): Promise<void>;
  deleteKv(cell: string, key: string): Promise<void>;
  listKv(cell: string): Promise<KvRecord[]>;
  getMeta(cell: string): Promise<CellMeta | undefined>;
  putMeta(meta: CellMeta): Promise<void>;
  deleteMeta(cell: string): Promise<void>;
  /** Least recently accessed first. */
  lru(kind: 'conv' | 'kv', limit: number): Promise<LruEntry[]>;
}

/** Storage behind the cache logic; a SQLite implementation can follow the same shape. */
export interface Backend {
  readonly kind: 'indexeddb' | 'memory';
  /** True when the connection was closed from outside (another tab upgrades or deletes the database). */
  readonly closed: boolean;
  /** One atomic transaction. The function must only await calls on its Tx. */
  transact<T>(fn: (tx: Tx) => Promise<T>): Promise<T>;
  close(): void;
}

const sep = '\u0000';

export function createMemoryBackend(): Backend {
  const convs = new Map<string, ConvHeader>();
  const messages = new Map<string, Map<number, StoredMessage>>();
  const kv = new Map<string, KvRecord>();
  const metas = new Map<string, CellMeta>();
  let totals: Totals = { bytes: 0, convs: 0 };

  const ck = (cell: string, conv: string) => `${cell}${sep}${conv}`;
  const inCell = (cell: string) => (key: string) => key.startsWith(`${cell}${sep}`);
  const byAccess = (a: { accessedAt: number }, b: { accessedAt: number }) => a.accessedAt - b.accessedAt;

  const tx: Tx = {
    getTotals: () => Promise.resolve({ ...totals }),
    setTotals(next) {
      totals = { ...next };
      return Promise.resolve();
    },
    getConv: (cell, conv) => Promise.resolve(convs.get(ck(cell, conv))),
    putConv(header) {
      convs.set(ck(header.cell, header.conv), header);
      return Promise.resolve();
    },
    deleteConv(cell, conv) {
      convs.delete(ck(cell, conv));
      messages.delete(ck(cell, conv));
      return Promise.resolve();
    },
    listConvs: (cell) => Promise.resolve([...convs].filter(([k]) => inCell(cell)(k)).map(([, v]) => v)),
    getMessage: (cell, conv, seq) => Promise.resolve(messages.get(ck(cell, conv))?.get(seq)),
    putMessage(message) {
      const key = ck(message.cell, message.conv);
      let bucket = messages.get(key);
      if (!bucket) {
        bucket = new Map();
        messages.set(key, bucket);
      }
      bucket.set(message.seq, { ...message, message: structuredClone(message.message) });
      return Promise.resolve();
    },
    listMessages: (cell, conv) =>
      Promise.resolve(
        [...(messages.get(ck(cell, conv))?.values() ?? [])]
          .sort((a, b) => a.seq - b.seq)
          .map((m) => ({ ...m, message: structuredClone(m.message) })),
      ),
    deleteOldest(cell, conv, count) {
      const bucket = messages.get(ck(cell, conv));
      let freed = 0;
      for (const m of [...(bucket?.values() ?? [])]
        .sort((a, b) => a.seq - b.seq)
        .slice(0, Math.max(0, count))) {
        freed += m.bytes;
        bucket?.delete(m.seq);
      }
      return Promise.resolve(freed);
    },
    getKv: (cell, key) => Promise.resolve(kv.get(ck(cell, key))),
    putKv(record) {
      kv.set(ck(record.cell, record.key), { ...record, value: structuredClone(record.value) });
      return Promise.resolve();
    },
    deleteKv(cell, key) {
      kv.delete(ck(cell, key));
      return Promise.resolve();
    },
    listKv: (cell) => Promise.resolve([...kv].filter(([k]) => inCell(cell)(k)).map(([, v]) => v)),
    getMeta: (cell) => Promise.resolve(structuredClone(metas.get(cell))),
    putMeta(meta) {
      metas.set(meta.cell, structuredClone(meta));
      return Promise.resolve();
    },
    deleteMeta(cell) {
      metas.delete(cell);
      return Promise.resolve();
    },
    lru(kind, limit) {
      const source = kind === 'conv' ? [...convs.values()] : [...kv.values()];
      return Promise.resolve(
        source
          .sort(byAccess)
          .slice(0, limit)
          .map((v) => ({
            cell: v.cell,
            id: 'conv' in v ? v.conv : v.key,
            bytes: v.bytes,
          })),
      );
    },
  };

  return {
    kind: 'memory',
    closed: false,
    transact: (fn) => fn(tx),
    close() {},
  };
}
