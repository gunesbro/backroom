import { describe, expect, it, vi } from 'vitest';
import { backoffWithJitter, createRealtimeClient } from './client';
import type { Hub } from './hub';

class FakeHub implements Hub {
  invoked: string[] = [];
  handlers = new Map<string, (payload: unknown) => void>();
  closeHandler: () => void = () => undefined;
  stopped = false;
  start = vi.fn(() => Promise.resolve());
  stop = vi.fn(() => {
    this.stopped = true;
    return Promise.resolve();
  });
  invoke = vi.fn((method: string, ...args: unknown[]) => {
    this.invoked.push([method, ...args].join(' '));
    return Promise.resolve(undefined);
  });
  on(event: string, handler: (payload: unknown) => void) {
    this.handlers.set(event, handler);
  }
  onClose(handler: () => void) {
    this.closeHandler = handler;
  }
  emit(event: string, payload: unknown) {
    this.handlers.get(event)?.(payload);
  }
  drop() {
    this.closeHandler();
  }
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

function setup() {
  const hubs: FakeHub[] = [];
  const urls: string[] = [];
  const retries: (() => void)[] = [];
  const delays: number[] = [];
  const client = createRealtimeClient({
    cellUrl: 'https://cell.example.test/',
    getAccessToken: () => Promise.resolve('token'),
    hubFactory: (url) => {
      urls.push(url);
      const hub = new FakeHub();
      hubs.push(hub);
      return Promise.resolve(hub);
    },
    retryDelay: (attempt) => attempt * 10,
    schedule: (callback, ms) => {
      delays.push(ms);
      retries.push(callback);
      return () => undefined;
    },
  });
  return { client, hubs, urls, retries, delays };
}

describe('RealtimeClient', () => {
  it('Start_ConnectsToTheHubPath', async () => {
    const { client, urls } = setup();
    client.start();
    await flush();

    expect(urls).toEqual(['https://cell.example.test/hubs/realtime']);
    expect(client.status()).toBe('connected');
  });

  it('Subscribe_BeforeAndAfterConnecting_JoinsTheGroupsOneAtATime', async () => {
    const { client, hubs } = setup();
    client.subscribeOrganization('o1');
    client.start();
    await flush();
    client.subscribeChannel('o1', 'c1');
    await flush();

    expect(hubs[0]?.invoked).toEqual(['SubscribeOrganization o1', 'SubscribeChannel o1 c1']);
  });

  it('Reconnect_AfterTheConnectionDrops_ResubscribesAndReportsAReconnect', async () => {
    const { client, hubs, retries } = setup();
    const seen: string[] = [];
    client.onStatus((status, reconnected) => seen.push(`${status}${reconnected ? '+re' : ''}`));
    client.subscribeOrganization('o1');
    client.subscribeChannel('o1', 'c1');
    client.start();
    await flush();

    hubs[0]?.drop();
    expect(client.status()).toBe('reconnecting');
    retries.shift()?.();
    await flush();

    expect(hubs).toHaveLength(2);
    expect(hubs[1]?.invoked).toEqual(['SubscribeOrganization o1', 'SubscribeChannel o1 c1']);
    expect(seen).toEqual(['connecting', 'connected', 'reconnecting', 'connected+re']);
  });

  it('Unsubscribe_ThenReconnect_DoesNotJoinTheGroupAgain', async () => {
    const { client, hubs, retries } = setup();
    client.subscribeChannel('o1', 'c1');
    client.start();
    await flush();
    client.unsubscribeChannel('o1', 'c1');
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[1]?.invoked).toEqual([]);
  });

  it('Reconnect_WhenAnAttemptFails_RetriesWithGrowingDelays', async () => {
    const hubs: FakeHub[] = [];
    const retries: (() => void)[] = [];
    const delays: number[] = [];
    let failures = 2;
    const client = createRealtimeClient({
      cellUrl: 'https://cell.example.test',
      getAccessToken: () => Promise.resolve('t'),
      hubFactory: () => {
        const hub = new FakeHub();
        if (failures > 0) {
          failures -= 1;
          hub.start.mockRejectedValueOnce(new Error('down'));
        }
        hubs.push(hub);
        return Promise.resolve(hub);
      },
      retryDelay: (attempt) => attempt * 10,
      schedule: (callback, ms) => {
        delays.push(ms);
        retries.push(callback);
        return () => undefined;
      },
    });
    client.start();
    await flush();
    retries.shift()?.();
    await flush();
    retries.shift()?.();
    await flush();

    expect(delays).toEqual([0, 10]);
    expect(client.status()).toBe('connected');
  });

  it('Events_FromTheHub_ReachListenersUntilUnsubscribed', async () => {
    const { client, hubs } = setup();
    const listener = vi.fn();
    const off = client.on('ChannelCreated', listener);
    client.start();
    await flush();
    const payload = { orgId: 'o1', channel: { id: 'c1', name: 'x', createdAt: '2026-11-22T10:00:00Z' } };

    hubs[0]?.emit('ChannelCreated', payload);
    off();
    hubs[0]?.emit('ChannelCreated', payload);

    expect(listener).toHaveBeenCalledTimes(1);
    expect(listener).toHaveBeenCalledWith(payload);
  });

  it('SessionRevoked_WhenTheServerCloses_DoesNotReconnect', async () => {
    const { client, hubs, retries } = setup();
    const listener = vi.fn();
    client.on('SessionRevoked', listener);
    client.start();
    await flush();

    hubs[0]?.emit('SessionRevoked', { reason: 'logout' });
    hubs[0]?.drop();

    expect(listener).toHaveBeenCalledWith({ reason: 'logout' });
    expect(retries).toHaveLength(0);
    expect(client.status()).toBe('stopped');
  });

  it('Stop_ClosesTheHubAndCancelsRetries', async () => {
    const { client, hubs } = setup();
    client.start();
    await flush();
    await client.stop();

    expect(hubs[0]?.stopped).toBe(true);
    expect(client.status()).toBe('stopped');
  });

  it('Subscribe_WhenTheServerAnswersNotFound_IsNotRepeatedAfterAReconnect', async () => {
    const { client, hubs, retries } = setup();
    client.start();
    await flush();
    hubs[0]?.invoke.mockRejectedValueOnce(new Error('An unexpected error. realtime.not_found'));
    client.subscribeChannel('o1', 'gone');
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[1]?.invoked).toEqual([]);
  });
});

describe('backoffWithJitter', () => {
  it('Attempt_Grows_AndNeverPassesThirtySeconds', () => {
    expect(backoffWithJitter(0, () => 1)).toBe(500);
    expect(backoffWithJitter(3, () => 1)).toBe(4000);
    expect(backoffWithJitter(40, () => 1)).toBe(30_000);
    expect(backoffWithJitter(5, () => 0)).toBe(0);
  });
});
