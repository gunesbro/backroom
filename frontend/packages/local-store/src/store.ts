import { createMemoryBackend, type Backend, type Header, type Put } from './backend.ts';
import { openIndexedDbBackend } from './indexeddb-backend.ts';
import {
  defaultLimits,
  type Cursors,
  type CursorsPatch,
  type Limits,
  type LocalStore,
  type LocalStoreOptions,
  type MessageLike,
} from './types.ts';

/** Bump when the stored layout changes; data of any other version is wiped. */
export const SCHEMA_VERSION = 1;

interface CellMeta {
  epoch?: string;
  feedCursor?: string;
  seqs: Record<string, number>;
}

const SEP = '\u0000';
const convKey = (cell: string, conv: string) => `c${SEP}${cell}${SEP}${conv}`;
const kvKey = (cell: string, key: string) => `k${SEP}${cell}${SEP}${key}`;
const metaKey = (cell: string) => `m${SEP}${cell}`;

function sizeOf(value: unknown): number {
  return JSON.stringify(value).length;
}

function byAccess(a: Header, b: Header): number {
  return a.accessedAt - b.accessedAt;
}

export async function createLocalStore<M extends MessageLike = MessageLike>(
  options: LocalStoreOptions,
): Promise<LocalStore<M>> {
  const limits: Limits = { ...defaultLimits, ...options.limits };
  const version = options.schemaVersion ?? SCHEMA_VERSION;
  const clock = options.now ?? (() => Date.now());
  const headers = new Map<string, Header>();
  let backend: Backend;
  let lastTick = 0;
  let queue: Promise<unknown> = Promise.resolve();

  const tick = () => {
    lastTick = Math.max(clock(), lastTick + 1);
    return lastTick;
  };

  function fallBack(reason: 'unavailable' | 'error'): void {
    try {
      backend.close();
    } catch {
      // Already unusable.
    }
    backend = createMemoryBackend();
    headers.clear();
    options.onFallback?.(reason);
  }

  const factory =
    options.indexedDB === undefined ? (globalThis.indexedDB as IDBFactory | undefined) : options.indexedDB;
  backend = createMemoryBackend();
  if (factory) {
    try {
      const opened = await openIndexedDbBackend(factory, `backroom-cache-${options.accountId}`, version);
      for (const header of await opened.readHeaders()) headers.set(header.key, header);
      backend = opened;
    } catch {
      headers.clear();
      options.onFallback?.('error');
    }
  } else {
    options.onFallback?.('unavailable');
  }

  /** Runs one operation at a time; a failing IndexedDB switches the store to memory and retries once. */
  function run<T>(operation: () => Promise<T>): Promise<T> {
    const result = queue.then(async () => {
      try {
        return await operation();
      } catch (error) {
        if (backend.kind === 'memory') throw error;
        fallBack('error');
        return operation();
      }
    });
    queue = result.catch(() => undefined);
    return result;
  }

  async function write(puts: readonly Put[], deletes: readonly string[]): Promise<void> {
    await backend.write(puts, deletes);
    for (const key of deletes) headers.delete(key);
    for (const put of puts) headers.set(put.header.key, put.header);
  }

  async function readMeta(cell: string): Promise<CellMeta> {
    const stored = (await backend.read(metaKey(cell))) as CellMeta | undefined;
    return stored ?? { seqs: {} };
  }

  function metaPut(cell: string, meta: CellMeta): Put {
    return {
      header: { key: metaKey(cell), cell, name: '', kind: 'meta', bytes: sizeOf(meta), accessedAt: tick() },
      value: meta,
    };
  }

  /** Deletes the keys and removes the seq cursors of deleted conversations. */
  async function remove(keys: readonly string[]): Promise<void> {
    const forgotten = new Map<string, string[]>();
    for (const key of keys) {
      const header = headers.get(key);
      if (header?.kind !== 'conv') continue;
      forgotten.set(header.cell, [...(forgotten.get(header.cell) ?? []), header.name]);
    }
    const puts: Put[] = [];
    for (const [cell, conversations] of forgotten) {
      if (!headers.has(metaKey(cell))) continue;
      const meta = await readMeta(cell);
      meta.seqs = Object.fromEntries(Object.entries(meta.seqs).filter(([id]) => !conversations.includes(id)));
      puts.push(metaPut(cell, meta));
    }
    await write(puts, keys);
  }

  async function enforce(protectedKey?: string): Promise<void> {
    const all = [...headers.values()];
    const convs = all.filter((h) => h.kind === 'conv' && h.key !== protectedKey).sort(byAccess);
    const kvs = all.filter((h) => h.kind === 'kv').sort(byAccess);
    const protectedHeader = protectedKey === undefined ? undefined : headers.get(protectedKey);
    const doomed: string[] = [];
    let convCount = all.filter((h) => h.kind === 'conv').length;
    while (convCount > limits.maxConversations && convs.length > 0) {
      const oldest = convs.shift();
      if (!oldest) break;
      doomed.push(oldest.key);
      convCount--;
    }
    let total =
      all.reduce((sum, h) => sum + h.bytes, 0) -
      doomed.reduce((sum, k) => sum + (headers.get(k)?.bytes ?? 0), 0);
    const candidates = [...convs, ...kvs, ...(protectedHeader ? [protectedHeader] : [])];
    while (total > limits.maxBytes && candidates.length > 0) {
      const next = candidates.shift();
      if (!next) break;
      doomed.push(next.key);
      total -= next.bytes;
    }
    if (doomed.length > 0) await remove(doomed);
  }

  async function storeMessages(
    cell: string,
    conv: string,
    incoming: readonly MessageLike[],
    replace: boolean,
  ): Promise<void> {
    const key = convKey(cell, conv);
    const byId = new Map<string, MessageLike>();
    if (!replace && headers.has(key)) {
      for (const message of ((await backend.read(key)) as MessageLike[] | undefined) ?? [])
        byId.set(message.id, message);
    }
    for (const message of incoming) byId.set(message.id, message);
    const ordered = [...byId.values()].sort((a, b) => a.seq - b.seq);
    const kept = ordered.slice(Math.max(0, ordered.length - limits.maxMessagesPerConversation));
    await write(
      [
        {
          header: { key, cell, name: conv, kind: 'conv', bytes: sizeOf(kept), accessedAt: tick() },
          value: kept,
        },
      ],
      [],
    );
    await enforce(key);
  }

  return {
    get kind() {
      return backend.kind;
    },

    getMessages: (cell, conv) =>
      run(async () => {
        const key = convKey(cell, conv);
        const header = headers.get(key);
        if (!header) return undefined;
        const messages = (await backend.read(key)) as M[] | undefined;
        if (!messages) {
          await write([], [key]);
          return undefined;
        }
        await write([{ header: { ...header, accessedAt: tick() } }], []);
        return messages;
      }),

    putPage: (cell, conv, messages) => run(() => storeMessages(cell, conv, messages, true)),
    mergeMessages: (cell, conv, messages) => run(() => storeMessages(cell, conv, messages, false)),

    getCursors: (cell) =>
      run(async (): Promise<Cursors> => {
        if (!headers.has(metaKey(cell))) return { conversationSeqs: {} };
        const meta = await readMeta(cell);
        return {
          ...(meta.epoch === undefined ? {} : { epoch: meta.epoch }),
          ...(meta.feedCursor === undefined ? {} : { feedCursor: meta.feedCursor }),
          conversationSeqs: meta.seqs,
        };
      }),

    setCursors: (cell, patch: CursorsPatch) =>
      run(async () => {
        const meta = headers.has(metaKey(cell)) ? await readMeta(cell) : { seqs: {} };
        if (patch.epoch !== undefined) meta.epoch = patch.epoch;
        if (patch.feedCursor !== undefined) meta.feedCursor = patch.feedCursor;
        const seqs = new Map(Object.entries(meta.seqs));
        for (const [conv, seq] of Object.entries(patch.conversationSeqs ?? {})) {
          seqs.delete(conv); // re-insert so the oldest entries stay first
          seqs.set(conv, seq);
        }
        const dropped = Math.max(0, seqs.size - limits.maxConversationCursors);
        meta.seqs = Object.fromEntries([...seqs].slice(dropped));
        await write([metaPut(cell, meta)], []);
      }),

    getKv: (cell, key) =>
      run(async () => {
        const storeKey = kvKey(cell, key);
        const header = headers.get(storeKey);
        if (!header) return undefined;
        const value = (await backend.read(storeKey));
        if (value === undefined) {
          await write([], [storeKey]);
          return undefined;
        }
        await write([{ header: { ...header, accessedAt: tick() } }], []);
        return value;
      }),

    setKv: (cell, key, value) =>
      run(async () => {
        const bytes = sizeOf(value);
        if (bytes > limits.maxKvValueBytes) return false;
        const storeKey = kvKey(cell, key);
        await write(
          [{ header: { key: storeKey, cell, name: key, kind: 'kv', bytes, accessedAt: tick() }, value }],
          [],
        );
        await enforce();
        return headers.has(storeKey);
      }),

    deleteKv: (cell, key) => run(() => write([], [kvKey(cell, key)])),

    purgeConversation: (cell, conv) => run(() => remove([convKey(cell, conv)])),

    purgeCell: (cell) =>
      run(() =>
        write(
          [],
          [...headers.values()].filter((h) => h.cell === cell).map((h) => h.key),
        ),
      ),

    clear: () =>
      run(async () => {
        await backend.clear();
        headers.clear();
      }),

    close() {
      backend.close();
    },
  };
}
