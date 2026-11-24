import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createLocalStore, type Limits, type LocalStore, type MessageLike } from './index.ts';

interface Msg extends MessageLike {
  readonly text: string;
}

const msg = (seq: number, text = `m${String(seq)}`, extra: Partial<Msg> = {}): Msg => ({
  id: `id${String(seq)}`,
  seq,
  text,
  ...extra,
});

/** Shared by every store of a test so that several stores order their accesses on one clock. */
let time = 0;
beforeEach(() => {
  time = 0;
});
afterEach(() => {
  vi.restoreAllMocks();
});

interface Extra {
  schemaVersion?: number;
  accountId?: string;
  markerStorage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
}

function open(
  factory: IDBFactory | null,
  limits: Partial<Limits> = {},
  extra: Extra = {},
): Promise<LocalStore<Msg>> {
  return createLocalStore<Msg>({
    accountId: extra.accountId ?? 'acct',
    indexedDB: factory,
    limits,
    ...(extra.schemaVersion === undefined ? {} : { schemaVersion: extra.schemaVersion }),
    ...(extra.markerStorage === undefined ? {} : { markerStorage: extra.markerStorage }),
    now: () => ++time,
  });
}

function fakeStorage(): Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> & {
  values: Map<string, string>;
} {
  const values = new Map<string, string>();
  return {
    values,
    getItem: (k) => values.get(k) ?? null,
    setItem: (k, v) => void values.set(k, v),
    removeItem: (k) => void values.delete(k),
  };
}

const seqsOf = async (store: LocalStore<Msg>, cell: string, conv: string) =>
  (await store.getMessages(cell, conv))?.map((m) => m.seq);

describe.each([
  ['indexeddb', () => new IDBFactory()],
  ['memory', () => null],
])('%s store', (kind, makeFactory) => {
  it('reports its kind', async () => {
    expect((await open(makeFactory())).kind).toBe(kind);
  });

  it('MergeMessages_WithOverlap_DedupesByIdAndOrdersBySeq', async () => {
    const store = await open(makeFactory());
    await store.mergeMessages('c1', 'a', [msg(3), msg(1)]);
    await store.mergeMessages('c1', 'a', [msg(2), msg(3, 'changed')]);
    const messages = await store.getMessages('c1', 'a');
    expect(messages?.map((m) => [m.seq, m.text])).toEqual([
      [1, 'm1'],
      [2, 'm2'],
      [3, 'changed'],
    ]);
  });

  it('GetMessages_WhenNotCached_ReturnsUndefined', async () => {
    expect(await (await open(makeFactory())).getMessages('c1', 'none')).toBeUndefined();
  });

  it('PutPage_ReplacesTheCachedPage', async () => {
    const store = await open(makeFactory());
    await store.putPage('c1', 'a', [msg(1), msg(2)]);
    await store.putPage('c1', 'a', [msg(5)]);
    expect(await seqsOf(store, 'c1', 'a')).toEqual([5]);
  });

  it('MergeMessages_OverMessageLimit_KeepsNewestBySeq', async () => {
    const store = await open(makeFactory(), { maxMessagesPerConversation: 3 });
    await store.mergeMessages(
      'c1',
      'a',
      [1, 2, 3, 4, 5].map((n) => msg(n)),
    );
    await store.mergeMessages('c1', 'a', [msg(6)]);
    expect(await seqsOf(store, 'c1', 'a')).toEqual([4, 5, 6]);
  });

  it('MergeMessages_WhenVersionIsLower_KeepsTheStoredCopy', async () => {
    const store = await open(makeFactory());
    await store.mergeMessages('c1', 'a', [msg(1, 'edited', { versionSeq: 5 })]);
    await store.mergeMessages('c1', 'a', [msg(1, 'stale', { versionSeq: 4 })]);
    expect((await store.getMessages('c1', 'a'))?.[0]?.text).toBe('edited');
    await store.mergeMessages('c1', 'a', [msg(1, 'same-version', { versionSeq: 5 })]);
    expect((await store.getMessages('c1', 'a'))?.[0]?.text).toBe('same-version');
  });

  it('MergeMessages_WithTombstone_AlwaysWinsAndContentIsNeverRestored', async () => {
    const store = await open(makeFactory());
    await store.mergeMessages('c1', 'a', [msg(1, 'secret', { versionSeq: 5 })]);
    await store.mergeMessages('c1', 'a', [msg(1, '', { versionSeq: 1, deletedAt: '2026-01-01T00:00:00Z' })]);
    await store.mergeMessages('c1', 'a', [msg(1, 'secret', { versionSeq: 9 })]);
    const stored = (await store.getMessages('c1', 'a'))?.[0];
    expect(stored?.deletedAt).toBe('2026-01-01T00:00:00Z');
    expect(stored?.text).toBe('');
  });

  it('PutPage_WithUnclonableMessage_Throws', async () => {
    const store = await open(makeFactory());
    const bad = { id: 'x', seq: 1, text: 'a', fn: () => 1 } as unknown as Msg;
    await expect(store.putPage('c1', 'a', [bad])).rejects.toMatchObject({ name: 'DataCloneError' });
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
  });

  it('PutPage_OverConversationLimit_EvictsLeastRecentlyUsedAndItsCursor', async () => {
    const store = await open(makeFactory(), { maxConversations: 2 });
    await store.putPage('c1', 'a', [msg(1)]);
    await store.putPage('c1', 'b', [msg(1)]);
    await store.setCursors('c1', { conversationSeqs: { a: 1, b: 1 } });
    await store.getMessages('c1', 'a'); // a is now more recent than b
    await store.putPage('c1', 'c', [msg(1)]);
    expect(await store.getMessages('c1', 'b')).toBeUndefined();
    expect(await store.getMessages('c1', 'a')).toBeDefined();
    expect(await store.getMessages('c1', 'c')).toBeDefined();
    expect((await store.getCursors('c1')).conversationSeqs).toEqual({ a: 1 });
  });

  it('PutPage_OverByteBudget_EvictsOldestFirstAndKeepsTheNewest', async () => {
    const text = 'x'.repeat(300);
    const store = await open(makeFactory(), { maxBytes: 800 });
    for (const conv of ['a', 'b', 'c', 'd']) await store.putPage('c1', conv, [msg(1, text)]);
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getMessages('c1', 'b')).toBeUndefined();
    expect(await store.getMessages('c1', 'c')).toBeDefined();
    expect(await store.getMessages('c1', 'd')).toBeDefined();
  });

  it('PutPage_WhenOneConversationExceedsBudget_DropsIt', async () => {
    const store = await open(makeFactory(), { maxBytes: 200 });
    await store.putPage('c1', 'a', [msg(1, 'x'.repeat(500))]);
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
  });

  it('SetKv_RoundTripsAndRejectsOversizedValues', async () => {
    const store = await open(makeFactory(), { maxKvValueBytes: 50 });
    expect(await store.setKv('c1', 'list', ['a', 'b'])).toBe(true);
    expect(await store.getKv('c1', 'list')).toEqual(['a', 'b']);
    expect(await store.setKv('c1', 'big', 'x'.repeat(100))).toBe(false);
    expect(await store.getKv('c1', 'big')).toBeUndefined();
    await store.deleteKv('c1', 'list');
    expect(await store.getKv('c1', 'list')).toBeUndefined();
  });

  it('SetKv_OverByteBudget_EvictsConversationsBeforeKv', async () => {
    const store = await open(makeFactory(), { maxBytes: 600 });
    await store.setKv('c1', 'list', 'y'.repeat(100));
    await store.putPage('c1', 'a', [msg(1, 'x'.repeat(400))]);
    await store.setKv('c1', 'other', 'z'.repeat(150));
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getKv('c1', 'list')).toBeDefined();
  });

  it('Cursors_MergeAndRoundTrip', async () => {
    const store = await open(makeFactory());
    expect(await store.getCursors('c1')).toEqual({ conversationSeqs: {} });
    await store.setCursors('c1', { epoch: 'e1', feedCursor: 'f1', conversationSeqs: { a: 4 } });
    await store.setCursors('c1', { feedCursor: 'f2', conversationSeqs: { b: 2 } });
    expect(await store.getCursors('c1')).toEqual({
      epoch: 'e1',
      feedCursor: 'f2',
      conversationSeqs: { a: 4, b: 2 },
    });
  });

  it('SetCursors_OverLimit_DropsTheOldest', async () => {
    const store = await open(makeFactory(), { maxConversationCursors: 2 });
    await store.setCursors('c1', { conversationSeqs: { a: 1, b: 1, c: 1 } });
    expect(Object.keys((await store.getCursors('c1')).conversationSeqs)).toEqual(['b', 'c']);
  });

  it('PurgeConversation_RemovesMessagesAndSeqOnly', async () => {
    const store = await open(makeFactory());
    await store.putPage('c1', 'a', [msg(1)]);
    await store.putPage('c1', 'b', [msg(1)]);
    await store.setCursors('c1', { epoch: 'e', conversationSeqs: { a: 1, b: 1 } });
    await store.purgeConversation('c1', 'a');
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getMessages('c1', 'b')).toBeDefined();
    expect(await store.getCursors('c1')).toEqual({ epoch: 'e', conversationSeqs: { b: 1 } });
  });

  it('PurgeConversation_WhenNothingIsCached_StillDropsTheSeqCursor', async () => {
    const store = await open(makeFactory());
    await store.setCursors('c1', { conversationSeqs: { a: 7 } });
    await store.purgeConversation('c1', 'a');
    expect((await store.getCursors('c1')).conversationSeqs).toEqual({});
  });

  it('PurgeCell_RemovesOnlyThatCell', async () => {
    const store = await open(makeFactory());
    for (const cell of ['c1', 'c2']) {
      await store.putPage(cell, 'a', [msg(1)]);
      await store.setKv(cell, 'list', [1]);
      await store.setCursors(cell, { epoch: 'e', conversationSeqs: { a: 1 } });
    }
    await store.purgeCell('c1');
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getKv('c1', 'list')).toBeUndefined();
    expect(await store.getCursors('c1')).toEqual({ conversationSeqs: {} });
    expect(await store.getMessages('c2', 'a')).toBeDefined();
    expect(await store.getKv('c2', 'list')).toBeDefined();
    expect((await store.getCursors('c2')).epoch).toBe('e');
  });

  it('Clear_OnSignOut_RemovesEverythingAndStaysUsable', async () => {
    const store = await open(makeFactory());
    await store.putPage('c1', 'a', [msg(1)]);
    await store.setKv('c1', 'list', [1]);
    await store.setCursors('c1', { epoch: 'e', conversationSeqs: { a: 1 } });
    await store.clear();
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getKv('c1', 'list')).toBeUndefined();
    expect(await store.getCursors('c1')).toEqual({ conversationSeqs: {} });
    await store.putPage('c1', 'a', [msg(2)]);
    expect(await seqsOf(store, 'c1', 'a')).toEqual([2]);
  });
});

describe('two tabs on one database', () => {
  it('Stores_ShareOneBudgetAndSeeEachOthersWrites', async () => {
    const factory = new IDBFactory();
    const tabA = await open(factory, { maxConversations: 2 });
    const tabB = await open(factory, { maxConversations: 2 });
    await tabA.putPage('c1', 'a', [msg(1)]);
    await tabA.putPage('c1', 'b', [msg(1)]);
    await tabB.putPage('c1', 'c', [msg(1)]); // evicts a for both tabs
    expect(await tabA.getMessages('c1', 'a')).toBeUndefined();
    expect(await tabA.getMessages('c1', 'c')).toBeDefined();
    expect(await tabB.getMessages('c1', 'b')).toBeDefined();
  });

  it('MergeMessages_FromBothTabsAtOnce_KeepsBoth', async () => {
    const factory = new IDBFactory();
    const tabA = await open(factory);
    const tabB = await open(factory);
    await Promise.all([tabA.mergeMessages('c1', 'a', [msg(1)]), tabB.mergeMessages('c1', 'a', [msg(2)])]);
    expect(await seqsOf(tabA, 'c1', 'a')).toEqual([1, 2]);
  });

  it('Purges_AreVisibleToTheOtherTab', async () => {
    const factory = new IDBFactory();
    const tabA = await open(factory);
    const tabB = await open(factory);
    await tabA.putPage('c1', 'a', [msg(1)]);
    await tabA.putPage('c1', 'b', [msg(1)]);
    await tabA.putPage('c2', 'a', [msg(1)]);
    await tabA.purgeConversation('c1', 'a');
    expect(await tabB.getMessages('c1', 'a')).toBeUndefined();
    await tabA.purgeCell('c1');
    expect(await tabB.getMessages('c1', 'b')).toBeUndefined();
    expect(await tabB.getMessages('c2', 'a')).toBeDefined();
  });

  it('Clear_InOneTab_EmptiesTheOtherTabWithoutBlocking', async () => {
    const factory = new IDBFactory();
    const tabA = await open(factory);
    const tabB = await open(factory);
    await tabA.putPage('c1', 'a', [msg(1)]);
    await tabB.getMessages('c1', 'a');
    await tabA.clear();
    expect(await tabB.getMessages('c1', 'a')).toBeUndefined();
    expect(tabB.kind).toBe('indexeddb');
    await tabB.putPage('c1', 'a', [msg(2)]);
    expect(await seqsOf(tabA, 'c1', 'a')).toEqual([2]);
  });

  it('Purge_InOneTab_DropsTheCopyOfAMemoryFallbackTab', async () => {
    const tabA = await open(null);
    const tabB = await open(null);
    await tabA.putPage('c1', 'a', [msg(1)]);
    await tabB.putPage('c1', 'a', [msg(1)]);
    await tabA.purgeConversation('c1', 'a');
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(await tabB.getMessages('c1', 'a')).toBeUndefined();
    tabA.close();
    tabB.close();
  });
});

describe('indexeddb lifecycle', () => {
  it('Reopen_KeepsDataAndLruOrder', async () => {
    const factory = new IDBFactory();
    const first = await open(factory, { maxConversations: 2 });
    await first.putPage('c1', 'a', [msg(1)]);
    await first.putPage('c1', 'b', [msg(1)]);
    await first.getMessages('c1', 'a');
    first.close();
    const second = await open(factory, { maxConversations: 2 });
    await second.putPage('c1', 'c', [msg(1)]);
    expect(await second.getMessages('c1', 'b')).toBeUndefined();
    expect(await second.getMessages('c1', 'a')).toBeDefined();
  });

  it('Accounts_AreIsolatedAndTheDatabaseNameIsAHash', async () => {
    const factory = new IDBFactory();
    const alice = await open(factory, {}, { accountId: 'alice-secret-id' });
    const bob = await open(factory, {}, { accountId: 'bob' });
    await alice.putPage('c1', 'a', [msg(1)]);
    expect(await bob.getMessages('c1', 'a')).toBeUndefined();
    const names = (await factory.databases()).map((d) => d.name ?? '');
    expect(names).toHaveLength(2);
    for (const name of names) expect(name).toMatch(/^backroom-cache-[0-9a-f]{32}$/);
  });

  it('Clear_DeletesTheDatabase', async () => {
    const factory = new IDBFactory();
    const spy = vi.spyOn(factory, 'deleteDatabase');
    const store = await open(factory);
    await store.putPage('c1', 'a', [msg(1)]);
    await store.clear();
    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.mock.calls[0]?.[0]).toMatch(/^backroom-cache-[0-9a-f]{32}$/);
  });

  it('Open_WithNewSchemaVersion_WipesTheOldData', async () => {
    const factory = new IDBFactory();
    const v1 = await open(factory, {}, { schemaVersion: 1 });
    await v1.putPage('c1', 'a', [msg(1)]);
    await v1.setCursors('c1', { epoch: 'e', conversationSeqs: {} });
    v1.close();
    const v2 = await open(factory, {}, { schemaVersion: 2 });
    expect(v2.kind).toBe('indexeddb');
    expect(await v2.getMessages('c1', 'a')).toBeUndefined();
    expect(await v2.getCursors('c1')).toEqual({ conversationSeqs: {} });
  });

  it('Open_WithOlderSchemaVersionThanStored_WipesAndKeepsWorking', async () => {
    const factory = new IDBFactory();
    const v3 = await open(factory, {}, { schemaVersion: 3 });
    await v3.putPage('c1', 'a', [msg(1)]);
    v3.close();
    const v1 = await open(factory, {}, { schemaVersion: 1 });
    expect(v1.kind).toBe('indexeddb');
    expect(await v1.getMessages('c1', 'a')).toBeUndefined();
    await v1.putPage('c1', 'a', [msg(2)]);
    expect(await v1.getMessages('c1', 'a')).toHaveLength(1);
  });

  it('VersionChange_FromAnotherTab_ClosesTheConnectionAndTheStoreReopens', async () => {
    const factory = new IDBFactory();
    const store = await open(factory);
    await store.putPage('c1', 'a', [msg(1)]);
    const name = (await factory.databases())[0]?.name ?? '';
    // Another tab upgrades the database; our connection must not block it.
    await new Promise<void>((resolve, reject) => {
      const upgrade = factory.open(name, 99);
      upgrade.onsuccess = () => {
        upgrade.result.close();
        resolve();
      };
      upgrade.onerror = () => {
        reject(upgrade.error ?? new Error('upgrade failed'));
      };
    });
    expect(await store.getMessages('c1', 'a')).toBeUndefined(); // unknown newer format was wiped
    expect(store.kind).toBe('indexeddb');
    await store.putPage('c1', 'a', [msg(2)]);
    expect(await seqsOf(store, 'c1', 'a')).toEqual([2]);
  });
});

describe('quota and failures', () => {
  it('Write_WhenQuotaIsExceeded_EvictsAndRetriesOnce', async () => {
    const store = await open(new IDBFactory());
    await store.putPage('c1', 'a', [msg(1, 'x'.repeat(200))]);
    await store.putPage('c1', 'b', [msg(1, 'x'.repeat(200))]);
    vi.spyOn(IDBObjectStore.prototype, 'put').mockImplementationOnce(() => {
      throw new DOMException('quota', 'QuotaExceededError');
    });
    await store.putPage('c1', 'c', [msg(1, 'x'.repeat(200))]);
    expect(store.kind).toBe('indexeddb');
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getMessages('c1', 'c')).toBeDefined();
  });

  it('Write_WhenQuotaStaysExceeded_GivesUpWithoutThrowingOrFallingBack', async () => {
    const store = await open(new IDBFactory());
    await store.putPage('c1', 'a', [msg(1)]);
    vi.spyOn(IDBObjectStore.prototype, 'put').mockImplementation(() => {
      throw new DOMException('quota', 'QuotaExceededError');
    });
    await expect(store.putPage('c1', 'b', [msg(1)])).resolves.toBeUndefined();
    expect(store.kind).toBe('indexeddb');
  });
});

describe('unavailable IndexedDB', () => {
  it('Open_WhenNoFactory_FallsBackToMemoryWithTheSameLimits', async () => {
    const onFallback = vi.fn();
    const store = await createLocalStore<Msg>({
      accountId: 'acct',
      indexedDB: null,
      limits: { maxConversations: 1 },
      onFallback,
    });
    expect(onFallback).toHaveBeenCalledWith('unavailable');
    await store.putPage('c1', 'a', [msg(1)]);
    await store.putPage('c1', 'b', [msg(1)]);
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getMessages('c1', 'b')).toBeDefined();
  });

  it('Open_WhenOpenThrows_FallsBackToMemory', async () => {
    const broken = {
      open: () => {
        throw new DOMException('blocked', 'SecurityError');
      },
    } as unknown as IDBFactory;
    const onFallback = vi.fn();
    const store = await createLocalStore<Msg>({ accountId: 'acct', indexedDB: broken, onFallback });
    expect(store.kind).toBe('memory');
    expect(onFallback).toHaveBeenCalledWith('error');
    await store.putPage('c1', 'a', [msg(1)]);
    expect(await store.getMessages('c1', 'a')).toHaveLength(1);
  });

  it('Operation_WhenTheDatabaseBecomesInvalid_FallsBackToMemory', async () => {
    const factory = new IDBFactory();
    const onFallback = vi.fn();
    const store = await createLocalStore<Msg>({ accountId: 'acct', indexedDB: factory, onFallback });
    vi.spyOn(IDBDatabase.prototype, 'transaction').mockImplementation(() => {
      throw new DOMException('closing', 'InvalidStateError');
    });
    await expect(store.putPage('c1', 'a', [msg(1)])).resolves.toBeUndefined();
    expect(store.kind).toBe('memory');
    expect(onFallback).toHaveBeenCalledWith('error');
    expect(await store.getMessages('c1', 'a')).toHaveLength(1);
  });

  async function fallenBackWithStoredData(markerStorage: ReturnType<typeof fakeStorage>) {
    const factory = new IDBFactory();
    const store = await open(factory, {}, { markerStorage });
    await store.putPage('c1', 'a', [msg(1)]);
    await store.putPage('c1', 'b', [msg(1)]);
    await store.setCursors('c1', { conversationSeqs: { a: 1 } });
    const failing = vi.spyOn(IDBDatabase.prototype, 'transaction').mockImplementation(() => {
      throw new DOMException('closing', 'InvalidStateError');
    });
    await store.putPage('c2', 'z', [msg(1)]); // triggers the fallback
    expect(store.kind).toBe('memory');
    return { factory, store, failing };
  }

  it('PurgeConversation_AfterFallback_AlsoPurgesIndexedDbWhenItWorksAgain', async () => {
    const markers = fakeStorage();
    const { factory, store, failing } = await fallenBackWithStoredData(markers);
    failing.mockRestore();
    await store.purgeConversation('c1', 'a');
    const fresh = await open(factory);
    expect(await fresh.getMessages('c1', 'a')).toBeUndefined();
    expect(await fresh.getMessages('c1', 'b')).toBeDefined();
    expect(markers.values.size).toBe(0);
  });

  it('PurgeCell_AfterFallbackWhenIndexedDbStillFails_DeletesTheDatabaseOnNextOpen', async () => {
    const markers = fakeStorage();
    const { factory, store } = await fallenBackWithStoredData(markers);
    await store.purgeCell('c1'); // IndexedDB still fails: a marker is left
    expect(markers.values.size).toBe(1);
    vi.restoreAllMocks();
    const next = await open(factory, {}, { markerStorage: markers });
    expect(next.kind).toBe('indexeddb');
    expect(await next.getMessages('c1', 'a')).toBeUndefined();
    expect(await next.getMessages('c1', 'b')).toBeUndefined();
    expect(markers.values.size).toBe(0);
  });

  it('Clear_AfterFallback_StillDeletesTheDatabase', async () => {
    const markers = fakeStorage();
    const { factory, store } = await fallenBackWithStoredData(markers);
    vi.restoreAllMocks();
    const spy = vi.spyOn(factory, 'deleteDatabase');
    await store.clear();
    expect(spy).toHaveBeenCalledTimes(1);
    expect(store.kind).toBe('indexeddb');
    expect(await (await open(factory)).getMessages('c1', 'a')).toBeUndefined();
  });
});
