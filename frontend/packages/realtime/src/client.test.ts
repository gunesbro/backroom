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

  it('Start_WithAPathOrManyTrailingSlashes_ConnectsToTheHubAtTheOrigin', async () => {
    const urls: string[] = [];
    const client = createRealtimeClient({
      cellUrl: `https://cell.example.test/some/path${'/'.repeat(50_000)}`,
      getAccessToken: () => Promise.resolve('token'),
      hubFactory: (url) => {
        urls.push(url);
        return Promise.resolve(new FakeHub());
      },
    });
    client.start();
    await flush();

    expect(urls).toEqual(['https://cell.example.test/hubs/realtime']);
  });

  it('Subscribe_BeforeAndAfterConnecting_JoinsTheGroupsOneAtATime', async () => {
    const { client, hubs } = setup();
    client.subscribeOrganization('o1');
    client.start();
    await flush();
    client.subscribeConversation('o1', 'c1');
    await flush();

    expect(hubs[0]?.invoked).toEqual(['SubscribeOrganization o1', 'SubscribeConversation o1 c1']);
  });

  it('Reconnect_AfterTheConnectionDrops_ResubscribesAndReportsAReconnect', async () => {
    const { client, hubs, retries } = setup();
    const seen: string[] = [];
    client.onStatus((status, reconnected) => seen.push(`${status}${reconnected ? '+re' : ''}`));
    client.subscribeOrganization('o1');
    client.subscribeConversation('o1', 'c1');
    client.start();
    await flush();

    hubs[0]?.drop();
    expect(client.status()).toBe('reconnecting');
    retries.shift()?.();
    await flush();

    expect(hubs).toHaveLength(2);
    expect(hubs[1]?.invoked).toEqual(['SubscribeOrganization o1', 'SubscribeConversation o1 c1']);
    expect(seen).toEqual(['connecting', 'connected', 'reconnecting', 'connected+re']);
  });

  it('Reconnect_ReportsConnectedOnlyAfterTheGroupsAreJoined', async () => {
    const hubs: FakeHub[] = [];
    const retries: (() => void)[] = [];
    const gates: (() => void)[] = [];
    const client = createRealtimeClient({
      cellUrl: 'https://cell.example.test',
      getAccessToken: () => Promise.resolve('t'),
      hubFactory: () => {
        const hub = new FakeHub();
        if (hubs.length > 0) {
          // After the first connection every join waits until the test lets it through.
          hub.invoke.mockImplementation((method: string, ...args: unknown[]) => {
            hub.invoked.push([method, ...args].join(' '));
            return new Promise<undefined>((resolve) => {
              gates.push(() => {
                resolve(undefined);
              });
            });
          });
        }
        hubs.push(hub);
        return Promise.resolve(hub);
      },
      retryDelay: () => 0,
      schedule: (callback) => {
        retries.push(callback);
        return () => undefined;
      },
    });
    const reconnected: number[] = [];
    client.onStatus((status, again) => {
      if (status === 'connected' && again) reconnected.push(gates.length);
    });
    client.subscribeOrganization('o1');
    client.subscribeConversation('o1', 'c1');
    client.start();
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    // Joined, not yet answered: not reported as connected, so no catch-up can start.
    expect(hubs[1]?.invoked).toEqual(['SubscribeOrganization o1']);
    expect(reconnected).toEqual([]);
    gates.shift()?.();
    await flush();
    gates.shift()?.();
    await flush();

    expect(hubs[1]?.invoked).toEqual(['SubscribeOrganization o1', 'SubscribeConversation o1 c1']);
    expect(reconnected).toHaveLength(1);
    expect(client.status()).toBe('connected');
  });

  it('Subscribe_WhenTheOrganizationIsRefused_DropsItsConversationsFromTheReplay', async () => {
    const { client, hubs, retries } = setup();
    client.start();
    await flush();
    hubs[0]?.invoke.mockRejectedValueOnce(new Error('realtime.not_found'));
    client.subscribeOrganization('o1');
    client.subscribeConversation('o1', 'c1');
    client.subscribeConversation('o2', 'c9');
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[1]?.invoked).toEqual(['SubscribeConversation o2 c9']);
  });

  it('Unsubscribe_ThenReconnect_DoesNotJoinTheGroupAgain', async () => {
    const { client, hubs, retries } = setup();
    client.subscribeConversation('o1', 'c1');
    client.start();
    await flush();
    client.unsubscribeConversation('o1', 'c1');
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
    client.subscribeConversation('o1', 'gone');
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[1]?.invoked).toEqual([]);
  });
});

describe('RealtimeClient conversation events after leaving', () => {
  const posted = (channelId: string) => ({
    orgId: 'o1',
    channelId,
    message: {
      id: 'm1',
      seq: 1,
      clientMessageId: 'x',
      authorAccountId: 'a',
      text: 'hi',
      createdAt: '2026-11-25T10:00:00Z',
    },
  });

  it('MessagePosted_AfterUnsubscribing_IsNotDelivered', async () => {
    const { client, hubs } = setup();
    const received: string[] = [];
    client.on('MessagePosted', (payload) => received.push(payload.channelId));
    client.subscribeConversation('o1', 'c1');
    client.start();
    await flush();

    hubs[0]?.emit('MessagePosted', posted('c1'));
    client.unsubscribeConversation('o1', 'c1');
    // Already on its way when the server took the connection out of the group.
    hubs[0]?.emit('MessagePosted', posted('c1'));
    hubs[0]?.emit('MessagePosted', posted('never-joined'));

    expect(received).toEqual(['c1']);
  });

  it('MembershipChanged_Removed_StopsDeliveryAndTheReplay', async () => {
    const { client, hubs, retries } = setup();
    const received: string[] = [];
    const changes: string[] = [];
    client.on('MessagePosted', (payload) => received.push(payload.channelId));
    client.on('MembershipChanged', (payload) => changes.push(`${payload.conversationId} ${payload.change}`));
    client.subscribeConversation('o1', 'c1');
    client.subscribeConversation('o1', 'c2');
    client.start();
    await flush();

    hubs[0]?.emit('MembershipChanged', { orgId: 'o1', conversationId: 'c1', change: 'removed' });
    hubs[0]?.emit('MessagePosted', posted('c1'));
    hubs[0]?.emit('MessagePosted', posted('c2'));
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(changes).toEqual(['c1 removed']);
    expect(received).toEqual(['c2']);
    expect(hubs[1]?.invoked).toEqual(['SubscribeConversation o1 c2']);
  });
});

describe('RealtimeClient queued joins', () => {
  it('Subscribe_QueuedForARefusedOrganization_IsNotSent', async () => {
    const { client, hubs } = setup();
    client.start();
    await flush();
    hubs[0]?.invoke.mockRejectedValueOnce(new Error('realtime.not_found'));
    client.subscribeOrganization('o1');
    client.subscribeConversation('o1', 'c1');
    client.subscribeConversation('o1', 'c2');
    await flush();

    // Only the organization was asked; its two conversations were dropped before they were sent.
    expect(hubs[0]?.invoke).toHaveBeenCalledTimes(1);
  });
});

describe('backoffWithJitter', () => {
  it('Attempt_Grows_AndNeverPassesThirtySeconds', () => {
    expect(backoffWithJitter(0, () => 1)).toBe(500);
    expect(backoffWithJitter(3, () => 1)).toBe(4000);
    expect(backoffWithJitter(40, () => 1)).toBe(30_000);
    expect(backoffWithJitter(5, () => 0)).toBe(0);
  });

  it('Start_StopThenStartWhileTheFirstHubIsStarting_KeepsOneHubAndSchedulesNoRetry', async () => {
    const hubs: FakeHub[] = [];
    const retries: (() => void)[] = [];
    const client = createRealtimeClient({
      cellUrl: 'https://cell.example.test/',
      getAccessToken: () => Promise.resolve('token'),
      hubFactory: () => {
        const hub = new FakeHub();
        if (hubs.length === 0) {
          // The first start stays in flight and fails when stop is called, as SignalR does.
          let reject: (error: Error) => void = () => undefined;
          hub.start = vi.fn(
            () =>
              new Promise<void>((_, rejectStart) => {
                reject = rejectStart;
              }),
          );
          hub.stop = vi.fn(() => {
            reject(new Error('stopped during start'));
            return Promise.resolve();
          });
        }
        hubs.push(hub);
        return Promise.resolve(hub);
      },
      schedule: (callback) => {
        retries.push(callback);
        return () => undefined;
      },
    });

    client.start();
    await flush();
    void client.stop();
    client.start();
    await flush();

    expect(retries).toHaveLength(0);
    expect(hubs).toHaveLength(2);
    expect(hubs[1]?.start).toHaveBeenCalledTimes(1);
    expect(hubs[1]?.stopped).toBe(false);
    expect(client.status()).toBe('connected');
  });
  it('WatchPresence_ReplacesTheSetAndIsReplayedAfterAReconnect', async () => {
    const { client, hubs, retries } = setup();
    client.watchPresence('o1', ['a', 'b', 'a']);
    client.start();
    await flush();
    client.watchPresence('o1', ['c']);
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[0]?.invoked).toEqual(['WatchPresence o1 a,b', 'WatchPresence o1 c']);
    expect(hubs[1]?.invoked).toEqual(['WatchPresence o1 c']);
  });

  it('WatchPresence_WithMoreThanTheLimit_SendsTheFirst200', async () => {
    const { client, hubs } = setup();
    client.start();
    await flush();
    client.watchPresence(
      'o1',
      Array.from({ length: 250 }, (_, index) => `id${String(index)}`),
    );
    await flush();

    const args = hubs[0]?.invoke.mock.calls[0]?.[2] as string[];
    expect(args).toHaveLength(200);
  });

  it('WatchPresence_WithAnEmptyList_StopsWatchingAndIsNotReplayed', async () => {
    const { client, hubs, retries } = setup();
    client.watchPresence('o1', ['a']);
    client.start();
    await flush();
    client.watchPresence('o1', []);
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[0]?.invoked).toEqual(['WatchPresence o1 a', 'WatchPresence o1 ']);
    expect(hubs[1]?.invoked).toEqual([]);
  });

  it('WatchPresence_WhenItsOrganizationIsRefused_IsDroppedFromTheReplay', async () => {
    const { client, hubs, retries } = setup();
    client.subscribeOrganization('o1');
    client.watchPresence('o1', ['a']);
    client.start();
    hubs[0]?.invoke.mockRejectedValueOnce(new Error('realtime.not_found'));
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[0]?.invoke).toHaveBeenCalledTimes(1);
    expect(hubs[1]?.invoked).toEqual([]);
  });

  it('WatchPresence_WhenRefused_IsNotAskedAgain', async () => {
    const { client, hubs, retries } = setup();
    client.start();
    await flush();
    hubs[0]?.invoke.mockRejectedValueOnce(new Error('realtime.not_found'));
    client.watchPresence('o2', ['a']);
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[1]?.invoked).toEqual([]);
  });

  it('ReportActivity_WhenConnected_InvokesTheHubAndIsNotReplayed', async () => {
    const { client, hubs, retries } = setup();
    client.reportActivity('idle');
    client.start();
    await flush();
    client.reportActivity('idle');
    await flush();
    hubs[0]?.drop();
    retries.shift()?.();
    await flush();

    expect(hubs[0]?.invoked).toEqual(['ReportActivity idle']);
    expect(hubs[1]?.invoked).toEqual([]);
  });

  it('PresenceChanged_ReachesItsListeners', async () => {
    const { client, hubs } = setup();
    const seen: unknown[] = [];
    client.on('PresenceChanged', (payload) => seen.push(payload));
    client.start();
    await flush();
    const payload = { orgId: 'o1', accountId: 'a', state: 'idle', dndUntil: null, customStatus: null };
    hubs[0]?.emit('PresenceChanged', payload);

    expect(seen).toEqual([payload]);
  });
});
