import { IDBFactory } from 'fake-indexeddb';
import { describe, expect, it, vi } from 'vitest';
import { createLocalStore, type Limits, type LocalStore, type MessageLike } from './index.ts';

interface Msg extends MessageLike {
  readonly text: string;
}

const msg = (seq: number, text = `m${String(seq)}`, id = `id${String(seq)}`): Msg => ({ id, seq, text });

async function open(
  factory: IDBFactory | null,
  limits: Partial<Limits> = {},
  extra: { schemaVersion?: number; accountId?: string } = {},
): Promise<LocalStore<Msg>> {
  let time = 0;
  return createLocalStore<Msg>({
    accountId: extra.accountId ?? 'acct',
    indexedDB: factory,
    limits,
    ...(extra.schemaVersion === undefined ? {} : { schemaVersion: extra.schemaVersion }),
    now: () => ++time,
  });
}

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
    expect((await store.getMessages('c1', 'a'))?.map((m) => m.seq)).toEqual([5]);
  });

  it('MergeMessages_OverMessageLimit_KeepsNewestBySeq', async () => {
    const store = await open(makeFactory(), { maxMessagesPerConversation: 3 });
    await store.mergeMessages(
      'c1',
      'a',
      [1, 2, 3, 4, 5].map((n) => msg(n)),
    );
    expect((await store.getMessages('c1', 'a'))?.map((m) => m.seq)).toEqual([3, 4, 5]);
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
    await store.putPage('c1', 'a', [msg(1, text)]);
    await store.putPage('c1', 'b', [msg(1, text)]);
    await store.putPage('c1', 'c', [msg(1, text)]);
    await store.putPage('c1', 'd', [msg(1, text)]);
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getMessages('c1', 'b')).toBeUndefined();
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

  it('Clear_OnSignOut_RemovesEverything', async () => {
    const store = await open(makeFactory());
    await store.putPage('c1', 'a', [msg(1)]);
    await store.setKv('c1', 'list', [1]);
    await store.setCursors('c1', { epoch: 'e', conversationSeqs: { a: 1 } });
    await store.clear();
    expect(await store.getMessages('c1', 'a')).toBeUndefined();
    expect(await store.getKv('c1', 'list')).toBeUndefined();
    expect(await store.getCursors('c1')).toEqual({ conversationSeqs: {} });
  });
});

describe('indexeddb persistence', () => {
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

  it('Accounts_AreIsolated', async () => {
    const factory = new IDBFactory();
    const alice = await open(factory, {}, { accountId: 'alice' });
    const bob = await open(factory, {}, { accountId: 'bob' });
    await alice.putPage('c1', 'a', [msg(1)]);
    expect(await bob.getMessages('c1', 'a')).toBeUndefined();
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

  it('Operation_WhenIndexedDbFailsLater_SwitchesToMemoryWithoutThrowing', async () => {
    const factory = new IDBFactory();
    const onFallback = vi.fn();
    const store = await createLocalStore<Msg>({ accountId: 'acct', indexedDB: factory, onFallback });
    await store.putPage('c1', 'a', [msg(1)]);
    // Closing the connection behind the store's back makes the next transaction throw.
    const open = factory.open('backroom-cache-acct');
    await new Promise<void>((resolve) => {
      open.onsuccess = () => {
        open.result.close();
        resolve();
      };
    });
    store.close();
    await expect(store.putPage('c1', 'b', [msg(1)])).resolves.toBeUndefined();
    expect(store.kind).toBe('memory');
    expect(onFallback).toHaveBeenCalledWith('error');
    expect(await store.getMessages('c1', 'b')).toHaveLength(1);
  });
});
