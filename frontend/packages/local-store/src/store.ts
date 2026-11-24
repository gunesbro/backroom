import { createMemoryBackend, type Backend, type CellMeta, type LruEntry, type Tx } from './backend.ts';
import { deleteIndexedDb, openIndexedDbBackend } from './indexeddb-backend.ts';
import {
  defaultLimits,
  type Cursors,
  type Limits,
  type LocalStore,
  type LocalStoreOptions,
  type MessageLike,
} from './types.ts';

/** Bump when the stored layout changes; data of any other version is wiped. */
export const SCHEMA_VERSION = 2;

const encoder = new TextEncoder();
function sizeOf(value: unknown): number {
  const json: unknown = JSON.stringify(value); // undefined for a bare undefined
  return typeof json === 'string' ? encoder.encode(json).length : 0;
}
const errorName = (error: unknown): string => {
  const name = (error as { name?: unknown } | null | undefined)?.name;
  return typeof name === 'string' ? name : '';
};

/** First 32 hex characters of SHA-256; the raw account id never appears in a database name. */
async function hashAccount(accountId: string): Promise<string | undefined> {
  const subtle = (globalThis.crypto as Crypto | undefined)?.subtle;
  if (!subtle) return undefined;
  const digest = await subtle.digest('SHA-256', encoder.encode(accountId));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0'))
    .join('')
    .slice(0, 32);
}

/** One operation at a time per database across tabs; an in-tab queue where Web Locks are missing. */
const queues = new Map<string, Promise<unknown>>();
function withLock<T>(name: string, fn: () => Promise<T>): Promise<T> {
  const locks = (globalThis.navigator as Navigator | undefined)?.locks;
  if (locks) return locks.request(name, fn);
  const run = (queues.get(name) ?? Promise.resolve()).then(fn);
  queues.set(
    name,
    run.catch(() => undefined),
  );
  return run;
}

function incomingWins(existing: MessageLike, incoming: MessageLike): boolean {
  if (incoming.deletedAt != null) return true;
  if (existing.deletedAt != null) return false;
  return (incoming.versionSeq ?? incoming.seq) >= (existing.versionSeq ?? existing.seq);
}

type Notice =
  | { type: 'conversation'; cell: string; conv: string }
  | { type: 'cell'; cell: string }
  | { type: 'clear' }
  | { type: 'open' };

let instanceCounter = 0;

export async function createLocalStore<M extends MessageLike = MessageLike>(
  options: LocalStoreOptions,
): Promise<LocalStore<M>> {
  const limits: Limits = { ...defaultLimits, ...options.limits };
  const version = options.schemaVersion ?? SCHEMA_VERSION;
  const clock = options.now ?? (() => Date.now());
  const factory =
    options.indexedDB === undefined ? (globalThis.indexedDB as IDBFactory | undefined) : options.indexedDB;
  const hash = await hashAccount(options.accountId);
  const dbName = hash === undefined ? undefined : `backroom-cache-${hash}`;
  const lockName = dbName ?? `backroom-cache-memory-${String(++instanceCounter)}`;
  const markerKey = hash === undefined ? undefined : `backroom-cache-purge-${hash}`;
  const markers =
    options.markerStorage !== undefined
      ? options.markerStorage
      : typeof window === 'undefined'
        ? null
        : safeLocalStorage();

  let lastTick = 0;
  const tick = () => {
    lastTick = Math.max(clock(), lastTick + 1);
    return lastTick;
  };

  let backend: Backend = createMemoryBackend();
  let fellBack = false;
  /** After clear() no database exists; the next write creates it again (reads see an empty cache). */
  let dormant = false;
  const retryDelaysMs = [1000, 5000, 30_000];
  let retryRound = 0;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;
  let pendingDelete = readMarker();

  function safeLocalStorage(): Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | null {
    try {
      return window.localStorage;
    } catch {
      return null;
    }
  }
  function readMarker(): boolean {
    try {
      return markerKey !== undefined && markers?.getItem(markerKey) === '1';
    } catch {
      return false;
    }
  }
  function setPending(value: boolean): void {
    pendingDelete = value;
    if (value) scheduleRetry();
    else {
      retryRound = 0;
      clearTimeout(retryTimer);
      retryTimer = undefined;
    }
    try {
      if (markerKey === undefined) return;
      if (value) markers?.setItem(markerKey, '1');
      else markers?.removeItem(markerKey);
    } catch {
      // The marker is best effort; the in-memory flag still retries during this session.
    }
  }

  function fallBack(reason: 'unavailable' | 'error'): void {
    backend.close();
    backend = createMemoryBackend();
    dormant = false;
    if (!fellBack) {
      fellBack = true;
      options.onFallback?.(reason);
    }
  }

  async function openIdb(): Promise<Backend | undefined> {
    if (!factory || !dbName) return undefined;
    try {
      return await openIndexedDbBackend(factory, dbName, version);
    } catch {
      return undefined;
    }
  }

  async function removeDatabase(): Promise<boolean> {
    if (!factory || !dbName) return true;
    try {
      await deleteIndexedDb(factory, dbName);
      return true;
    } catch {
      return false;
    }
  }

  /** A purge that could not reach IndexedDB leaves a marker; the database is deleted as soon as that works. */
  async function settlePending(): Promise<void> {
    if (!pendingDelete || backend.kind === 'indexeddb') return;
    if (await removeDatabase()) setPending(false);
  }

  /** Retries off the hot path with backoff (1 s, 5 s, 30 s); after that the next open settles it. */
  function scheduleRetry(): void {
    const delay = retryDelaysMs[retryRound];
    if (!pendingDelete || retryTimer !== undefined || delay === undefined) return;
    retryTimer = setTimeout(() => {
      retryTimer = undefined;
      retryRound++;
      void settlePending().then(scheduleRetry);
    }, delay);
    (retryTimer as { unref?: () => void }).unref?.();
  }

  if (!factory || !dbName) {
    fallBack('unavailable');
  } else {
    await settlePending();
    const opened = pendingDelete ? undefined : await openIdb();
    if (opened) backend = opened;
    else fallBack('error');
    scheduleRetry();
  }

  /** Leaves the state after clear(): opens the database again (a write does, and tells the other tabs). */
  async function wake(announceOpen: boolean): Promise<void> {
    if (!dormant) return;
    dormant = false;
    const reopened = await openIdb();
    if (reopened) backend = reopened;
    else fallBack('error');
    if (announceOpen) announce({ type: 'open' });
  }

  async function ensureOpen(): Promise<void> {
    if (backend.kind === 'indexeddb' && backend.closed) {
      const reopened = await openIdb();
      if (reopened) backend = reopened;
      else fallBack('error');
    }
  }

  /** After a fallback the database may still hold what was purged; try once, otherwise delete it later. */
  async function purgeStoredCopy(op: (tx: Tx) => Promise<void>): Promise<void> {
    if (!factory || !dbName || pendingDelete) return;
    const copy = await openIdb();
    try {
      if (!copy) throw new Error('indexeddb unavailable');
      await copy.transact(op);
    } catch {
      setPending(true);
    } finally {
      copy?.close();
    }
  }

  // Tabs on the same database tell each other about purges so memory fallbacks drop their copy.
  const channel =
    dbName !== undefined && typeof BroadcastChannel !== 'undefined'
      ? new BroadcastChannel(dbName)
      : undefined;
  (channel as { unref?: () => void } | undefined)?.unref?.();
  function announce(notice: Notice): void {
    try {
      channel?.postMessage(notice);
    } catch {
      // Closed channel; nothing to tell.
    }
  }
  if (channel) {
    channel.onmessage = (event: MessageEvent<Notice>) => {
      const notice = event.data;
      void withLock(lockName, async () => {
        if (notice.type === 'clear') {
          // The sender deleted the database; do not recreate it until something is written.
          backend.close();
          backend = createMemoryBackend();
          dormant = factory !== undefined && factory !== null;
          return;
        }
        if (notice.type === 'open') {
          await wake(false);
          return;
        }
        if (dormant) return;
        // Idempotent, so every backend applies it, IndexedDB included: the sender may have failed to reach it.
        await attempt(
          notice.type === 'cell' ? purgeCellOp(notice.cell) : purgeConversationOp(notice.cell, notice.conv),
          undefined,
          false,
        );
      }).catch(() => undefined);
    };
  }

  async function attempt<T>(op: (tx: Tx) => Promise<T>, giveUp: T, writes: boolean): Promise<T> {
    let quotaRetried = false;
    for (let round = 0; round < 3; round++) {
      if (writes) await wake(true);
      await ensureOpen();
      try {
        return await backend.transact(op);
      } catch (error) {
        const name = errorName(error);
        if (name === 'DataCloneError' || backend.kind === 'memory') throw error;
        if (name === 'QuotaExceededError') {
          if (quotaRetried) return giveUp;
          quotaRetried = true;
          try {
            await backend.transact(shrinkOp);
          } catch {
            return giveUp;
          }
        } else if (name === 'InvalidStateError' || name === 'UnknownError') {
          fallBack('error');
        } else {
          throw error;
        }
      }
    }
    return giveUp;
  }

  const exec = <T>(op: (tx: Tx) => Promise<T>, giveUp: T, writes = false): Promise<T> =>
    withLock(lockName, () => attempt(op, giveUp, writes));

  const purge = (op: (tx: Tx) => Promise<void>, notice: Notice): Promise<void> =>
    withLock(lockName, async () => {
      await attempt(op, undefined, false);
      if (backend.kind === 'memory' && !dormant) await purgeStoredCopy(op);
      announce(notice);
    });

  // Eviction -----------------------------------------------------------------------------------

  async function dropCursor(tx: Tx, cell: string, conv: string): Promise<void> {
    const meta = await tx.getMeta(cell);
    if (!meta || !(conv in meta.seqs)) return;
    meta.seqs = Object.fromEntries(Object.entries(meta.seqs).filter(([id]) => id !== conv));
    await tx.putMeta(meta);
  }

  async function evict(
    tx: Tx,
    totals: { bytes: number; convs: number },
    kind: 'conv' | 'kv',
    entry: LruEntry,
  ): Promise<void> {
    totals.bytes -= entry.bytes;
    if (kind === 'conv') {
      totals.convs--;
      await tx.deleteConv(entry.cell, entry.id);
      await dropCursor(tx, entry.cell, entry.id);
    } else {
      await tx.deleteKv(entry.cell, entry.id);
    }
  }

  /** Least recently used conversations first, then key-value entries, the just-written conversation last. */
  async function enforce(
    tx: Tx,
    totals: { bytes: number; convs: number },
    byteLimit: number,
    keep?: { cell: string; conv: string },
  ): Promise<void> {
    const notKept = (e: LruEntry) => !(keep && e.cell === keep.cell && e.id === keep.conv);
    while (totals.convs > limits.maxConversations) {
      const victim = (await tx.lru('conv', 2)).find(notKept);
      if (!victim) break;
      await evict(tx, totals, 'conv', victim);
    }
    while (totals.bytes > byteLimit) {
      const victim = (await tx.lru('conv', 2)).find(notKept);
      if (victim) {
        await evict(tx, totals, 'conv', victim);
        continue;
      }
      const oldestKv = (await tx.lru('kv', 1))[0];
      if (oldestKv) {
        await evict(tx, totals, 'kv', oldestKv);
        continue;
      }
      const own = keep && (await tx.getConv(keep.cell, keep.conv));
      if (!own) break;
      await evict(tx, totals, 'conv', { cell: own.cell, id: own.conv, bytes: own.bytes });
    }
  }

  /** Frees half of what is stored; used after the browser reports its quota is exceeded. */
  async function shrinkOp(tx: Tx): Promise<void> {
    const totals = await tx.getTotals();
    await enforce(tx, totals, Math.floor(totals.bytes / 2));
    await tx.setTotals(totals);
  }

  // Operations -----------------------------------------------------------------------------------

  function storeMessages(
    cell: string,
    conv: string,
    messages: readonly M[],
    replace: boolean,
  ): Promise<void> {
    const bySeq = new Map<number, { seq: number; bytes: number; message: M }>();
    for (const message of messages) {
      if (!Number.isFinite(message.seq)) continue;
      const earlier = bySeq.get(message.seq);
      if (earlier && !incomingWins(earlier.message, message)) continue;
      bySeq.set(message.seq, { seq: message.seq, bytes: sizeOf(message), message });
    }
    const ordered = [...bySeq.values()].sort((a, b) => a.seq - b.seq);
    const prepared = ordered.slice(Math.max(0, ordered.length - limits.maxMessagesPerConversation));

    return exec(
      async (tx) => {
        const totals = await tx.getTotals();
        let header = await tx.getConv(cell, conv);
        if (replace && header) {
          await tx.deleteConv(cell, conv);
          totals.bytes -= header.bytes;
          totals.convs--;
          header = undefined;
        }
        const before = header?.bytes ?? 0;
        let count = header?.count ?? 0;
        let bytes = before;
        if (!header) totals.convs++;
        for (const item of prepared) {
          const existing = await tx.getMessage(cell, conv, item.seq);
          if (existing) {
            if (!incomingWins(existing.message as MessageLike, item.message)) continue;
            bytes += item.bytes - existing.bytes;
          } else {
            count++;
            bytes += item.bytes;
          }
          await tx.putMessage({ cell, conv, seq: item.seq, bytes: item.bytes, message: item.message });
        }
        if (count > limits.maxMessagesPerConversation) {
          bytes -= await tx.deleteOldest(cell, conv, count - limits.maxMessagesPerConversation);
          count = limits.maxMessagesPerConversation;
        }
        totals.bytes += bytes - before;
        await tx.putConv({ cell, conv, count, bytes, accessedAt: tick() });
        await enforce(tx, totals, limits.maxBytes, { cell, conv });
        await tx.setTotals(totals);
      },
      undefined,
      true,
    );
  }

  function purgeConversationOp(cell: string, conv: string) {
    return async (tx: Tx): Promise<void> => {
      const totals = await tx.getTotals();
      const header = await tx.getConv(cell, conv);
      if (header) {
        await tx.deleteConv(cell, conv);
        totals.bytes -= header.bytes;
        totals.convs--;
        await tx.setTotals(totals);
      }
      await dropCursor(tx, cell, conv); // always, even when no messages were cached
    };
  }

  function purgeCellOp(cell: string) {
    return async (tx: Tx): Promise<void> => {
      const totals = await tx.getTotals();
      for (const header of await tx.listConvs(cell)) {
        await tx.deleteConv(cell, header.conv);
        totals.bytes -= header.bytes;
        totals.convs--;
      }
      for (const record of await tx.listKv(cell)) {
        await tx.deleteKv(cell, record.key);
        totals.bytes -= record.bytes;
      }
      await tx.deleteMeta(cell);
      await tx.setTotals(totals);
    };
  }

  return {
    get kind() {
      return dormant ? 'indexeddb' : backend.kind;
    },

    getMessages: (cell, conv) =>
      exec(async (tx) => {
        const header = await tx.getConv(cell, conv);
        if (!header) return undefined;
        const stored = await tx.listMessages(cell, conv);
        await tx.putConv({ ...header, accessedAt: tick() });
        return stored.map((s) => s.message as M);
      }, undefined),

    putPage: (cell, conv, messages) => storeMessages(cell, conv, messages, true),
    mergeMessages: (cell, conv, messages) => storeMessages(cell, conv, messages, false),

    getCursors: (cell) =>
      exec(
        async (tx): Promise<Cursors> => {
          const meta = await tx.getMeta(cell);
          return {
            ...(meta?.epoch === undefined ? {} : { epoch: meta.epoch }),
            ...(meta?.feedCursor === undefined ? {} : { feedCursor: meta.feedCursor }),
            conversationSeqs: meta?.seqs ?? {},
          };
        },
        { conversationSeqs: {} },
      ),

    setCursors: (cell, patch) =>
      exec(
        async (tx) => {
          const meta: CellMeta = (await tx.getMeta(cell)) ?? { cell, seqs: {} };
          if (patch.epoch !== undefined) meta.epoch = patch.epoch;
          if (patch.feedCursor !== undefined) meta.feedCursor = patch.feedCursor;
          const seqs = new Map(Object.entries(meta.seqs));
          for (const [conv, seq] of Object.entries(patch.conversationSeqs ?? {})) {
            seqs.delete(conv); // re-insert so the oldest entries stay first
            seqs.set(conv, seq);
          }
          const dropped = Math.max(0, seqs.size - limits.maxConversationCursors);
          meta.seqs = Object.fromEntries([...seqs].slice(dropped));
          await tx.putMeta(meta);
        },
        undefined,
        true,
      ),

    getKv: (cell, key) =>
      exec(async (tx) => {
        const record = await tx.getKv(cell, key);
        if (!record) return undefined;
        await tx.putKv({ ...record, accessedAt: tick() });
        return record.value;
      }, undefined),

    setKv: (cell, key, value) => {
      const bytes = sizeOf(value);
      if (bytes > limits.maxKvValueBytes) return Promise.resolve(false);
      return exec(
        async (tx) => {
          const totals = await tx.getTotals();
          const existing = await tx.getKv(cell, key);
          totals.bytes += bytes - (existing?.bytes ?? 0);
          await tx.putKv({ cell, key, value, bytes, accessedAt: tick() });
          await enforce(tx, totals, limits.maxBytes);
          await tx.setTotals(totals);
          return (await tx.getKv(cell, key)) !== undefined;
        },
        false,
        true,
      );
    },

    deleteKv: (cell, key) =>
      exec(async (tx) => {
        const existing = await tx.getKv(cell, key);
        if (!existing) return;
        const totals = await tx.getTotals();
        totals.bytes -= existing.bytes;
        await tx.deleteKv(cell, key);
        await tx.setTotals(totals);
      }, undefined),

    purgeConversation: (cell, conv) =>
      purge(purgeConversationOp(cell, conv), { type: 'conversation', cell, conv }),

    purgeCell: (cell) => purge(purgeCellOp(cell), { type: 'cell', cell }),

    clear: () =>
      withLock(lockName, async () => {
        backend.close();
        backend = createMemoryBackend();
        if (await removeDatabase()) {
          setPending(false);
          dormant = factory !== undefined && factory !== null && dbName !== undefined; // reopened lazily by the next write; no empty database is left after sign-out
        } else {
          setPending(true); // keep working in memory; the database goes as soon as it can
          fallBack('error');
        }
        announce({ type: 'clear' });
      }),

    close() {
      clearTimeout(retryTimer);
      backend.close();
      channel?.close();
    },
  };
}
