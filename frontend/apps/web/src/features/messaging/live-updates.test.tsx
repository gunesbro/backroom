import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { StrictMode } from 'react';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import {
  createRealtimeClient,
  type ConnectionStatus,
  type Hub,
  type RealtimeClient,
  type RealtimeEventName,
  type RealtimeEvents,
} from '@backroom/realtime';
import { AuthProvider } from '@/features/auth';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { MessagingApiContext, type Message, type MessageCursor, type MessagingApi } from './api';
import { applyLiveMessage, catchUpChannel, MAX_CATCH_UP_PAGES } from './live-cache';
import { messagesKey, type MessagePages } from './queries';
import { RealtimeContext } from './realtime-context';
import { forgetSeqs, lastSeenSeq, rememberSeq } from './seq-memory';
import { Workspace } from './workspace';

const ORG = 'org-1';
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const OTHER_ID = '00000000-0000-4000-8000-000000000002';
const GENERAL = { id: 'c-general', name: 'general', createdAt: '2026-11-20T09:00:00Z' };
const RANDOM = { id: 'c-random', name: 'random', createdAt: '2026-11-20T09:05:00Z' };

const message = (seq: number, extra: Partial<Message> = {}): Message => ({
  id: `m-${String(seq)}`,
  seq,
  clientMessageId: `client-${String(seq)}`,
  authorAccountId: OTHER_ID,
  text: `message ${String(seq)}`,
  createdAt: '2026-11-22T10:00:00Z',
  ...extra,
});

/** A hub client the test drives by hand. */
function fakeRealtime() {
  const listeners: { [K in RealtimeEventName]: ((payload: RealtimeEvents[K]) => void)[] } = {
    MessagePosted: [],
    ChannelCreated: [],
    SessionRevoked: [],
    MembershipChanged: [],
  };
  const statusListeners: ((status: ConnectionStatus, reconnected: boolean) => void)[] = [];
  let status: ConnectionStatus = 'stopped';
  const client = {
    start: vi.fn(() => {
      status = 'connected';
    }),
    stop: vi.fn(() => Promise.resolve()),
    subscribeOrganization: vi.fn(),
    unsubscribeOrganization: vi.fn(),
    subscribeConversation: vi.fn(),
    unsubscribeConversation: vi.fn(),
    on: (event: RealtimeEventName, listener: (payload: never) => void) => {
      (listeners[event] as ((payload: never) => void)[]).push(listener);
      return () => undefined;
    },
    onStatus: (listener: (status: ConnectionStatus, reconnected: boolean) => void) => {
      statusListeners.push(listener);
      return () => undefined;
    },
    status: () => status,
  };
  return {
    client: client as unknown as RealtimeClient,
    spies: client,
    emit: <K extends RealtimeEventName>(event: K, payload: RealtimeEvents[K]) => {
      act(() => {
        for (const listener of listeners[event]) listener(payload);
      });
    },
    setStatus: (next: ConnectionStatus, reconnected = false) => {
      act(() => {
        status = next;
        for (const listener of statusListeners) listener(next, reconnected);
      });
    },
  };
}

/** A cell with `count` messages (seq 1..count) in GENERAL; `after` pages answer newest first, as the API client does. */
function fakeApi(count = 3, extra: Partial<MessagingApi> = {}) {
  const calls: MessageCursor[] = [];
  let newest = count;
  const api: MessagingApi = {
    listChannels: () => Promise.resolve({ channels: [GENERAL, RANDOM], next: null }),
    createChannel: vi.fn(),
    listMessages: vi.fn((_org: string, _channel: string, cursor: MessageCursor) => {
      calls.push(cursor);
      if (cursor && 'after' in cursor) {
        const seqs: number[] = [];
        for (let seq = cursor.after + 1; seq <= newest && seqs.length < 50; seq += 1) seqs.unshift(seq);
        const top = seqs[0] ?? 0;
        return Promise.resolve({
          messages: seqs.map((seq) => message(seq)),
          next: top < newest ? top : null,
        });
      }
      const seqs: number[] = [];
      for (let seq = newest; seq >= 1 && seqs.length < 50; seq -= 1) seqs.push(seq);
      return Promise.resolve({ messages: seqs.map((seq) => message(seq)), next: null });
    }),
    sendMessage: vi.fn(),
    listMembers: () =>
      Promise.resolve({
        members: [
          { accountId: OWN_ID, displayName: 'Alice Example', email: 'alice@example.test' },
          { accountId: OTHER_ID, displayName: 'Veli Yilmaz', email: null },
        ],
        next: null,
      }),
    ...extra,
  };
  return {
    api,
    calls,
    grow: (to: number) => {
      newest = to;
    },
  };
}

beforeAll(() => {
  // jsdom has no layout: the log is 400 px tall, a row 40 px, and scrollTop can be set.
  const tops = new WeakMap<Element, number>();
  const isLog = (element: Element) => element.getAttribute('role') === 'feed';
  Object.defineProperties(HTMLElement.prototype, {
    offsetHeight: {
      configurable: true,
      get: function (this: HTMLElement) {
        return isLog(this) ? 400 : 40;
      },
    },
    offsetWidth: { configurable: true, get: () => 600 },
    clientHeight: {
      configurable: true,
      get: function (this: HTMLElement) {
        return isLog(this) ? 400 : 0;
      },
    },
    scrollHeight: {
      configurable: true,
      get: function (this: HTMLElement) {
        return isLog(this) ? Number.parseInt(this.querySelector('ol')?.style.height ?? '0', 10) : 0;
      },
    },
    scrollTop: {
      configurable: true,
      get: function (this: HTMLElement) {
        return tops.get(this) ?? 0;
      },
      set: function (this: HTMLElement, value: number) {
        tops.set(this, value);
      },
    },
  });
});

function renderLive(api: MessagingApi, realtime: RealtimeClient) {
  forgetSeqs();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const signOut = vi.fn(() => Promise.resolve());
  render(
    <LocaleProvider locale="en">
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={fakeAuthClient({ signOut })}>
          <MessagingApiContext.Provider value={() => api}>
            <RealtimeContext.Provider value={() => realtime}>
              <Workspace
                organization={{ id: ORG, name: 'Acme', cell: { id: 'a', url: 'https://cell.example.test' } }}
              />
            </RealtimeContext.Provider>
          </MessagingApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>,
  );
  return { queryClient, signOut };
}

async function openLog() {
  const log = await screen.findByRole('feed', { name: 'Messages in #general' });
  await waitFor(() => {
    expect(within(log).queryAllByRole('article').length).toBeGreaterThan(0);
  });
  fireEvent.scroll(log);
  return log;
}

const texts = (log: HTMLElement) =>
  within(log)
    .getAllByRole('article')
    .map((item) => /message \d+/.exec(item.textContent)?.[0]);

describe('Live updates in the open channel', () => {
  it('MessagePosted_ForTheOpenChannel_IsInsertedOnceAndInSeqOrder', async () => {
    const live = fakeRealtime();
    renderLive(fakeApi(3).api, live.client);
    const log = await openLog();

    live.emit('MessagePosted', { orgId: ORG, channelId: GENERAL.id, message: message(5) });
    live.emit('MessagePosted', { orgId: ORG, channelId: GENERAL.id, message: message(4) });
    live.emit('MessagePosted', { orgId: ORG, channelId: GENERAL.id, message: message(5) });

    await waitFor(() => {
      expect(texts(log)).toEqual(['message 1', 'message 2', 'message 3', 'message 4', 'message 5']);
    });
    // Live events never move the catch-up position: only a read of the server does.
    expect(lastSeenSeq(ORG, GENERAL.id)).toBe(3);
  });

  it('MessagePosted_AfterADroppedEvent_IsFilledInByTheNextCatchUp', async () => {
    const live = fakeRealtime();
    const { api, calls, grow } = fakeApi(3);
    renderLive(api, live.client);
    const log = await openLog();

    // Seq 4 was dropped; 5 arrives.
    grow(5);
    live.emit('MessagePosted', { orgId: ORG, channelId: GENERAL.id, message: message(5) });
    live.setStatus('connected', true);

    await waitFor(() => {
      expect(texts(log)).toEqual(['message 1', 'message 2', 'message 3', 'message 4', 'message 5']);
    });
    expect(calls).toContainEqual({ after: 3 });
  });

  it('MessagePosted_WithTheClientMessageIdOfAnOptimisticSend_ShowsTheMessageOnce', async () => {
    const live = fakeRealtime();
    let finish: (message: Message) => void = () => undefined;
    const { api } = fakeApi(3, {
      sendMessage: vi.fn(
        () =>
          new Promise<Message>((resolve) => {
            finish = resolve;
          }),
      ),
    });
    renderLive(api, live.client);
    const log = await openLog();

    fireEvent.change(screen.getByRole('textbox', { name: 'Message #general' }), {
      target: { value: 'hello there' },
    });
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Message #general' }), { key: 'Enter' });
    await waitFor(() => {
      expect(api.sendMessage).toHaveBeenCalled();
    });
    const sent = vi.mocked(api.sendMessage).mock.calls[0]?.[2];
    const own = message(4, {
      id: 'm-own',
      clientMessageId: sent?.clientMessageId ?? '',
      authorAccountId: OWN_ID,
      text: 'hello there',
    });

    // The live event beats the HTTP answer; then the answer arrives.
    live.emit('MessagePosted', { orgId: ORG, channelId: GENERAL.id, message: own });
    await act(async () => {
      finish(own);
      await Promise.resolve();
    });

    await waitFor(() => {
      expect(within(log).getAllByText('hello there')).toHaveLength(1);
    });
  });

  it('Reconnect_AfterAMissedMessage_CatchesUpFromTheLastSeenSeq', async () => {
    const live = fakeRealtime();
    const { api, calls, grow } = fakeApi(3);
    renderLive(api, live.client);
    const log = await openLog();

    grow(5);
    live.setStatus('reconnecting');
    live.setStatus('connected', true);

    await waitFor(() => {
      expect(texts(log)).toEqual(['message 1', 'message 2', 'message 3', 'message 4', 'message 5']);
    });
    expect(calls).toContainEqual({ after: 3 });
  });

  it('Subscriptions_OnOpening_JoinTheOrganizationAndItsChannels', async () => {
    const live = fakeRealtime();
    renderLive(fakeApi(3).api, live.client);
    await openLog();

    expect(live.spies.start).toHaveBeenCalled();
    expect(live.spies.subscribeOrganization).toHaveBeenCalledWith(ORG);
    expect(live.spies.subscribeConversation).toHaveBeenCalledWith(ORG, GENERAL.id);
    expect(live.spies.subscribeConversation).toHaveBeenCalledWith(ORG, RANDOM.id);
  });
});

describe('Live updates elsewhere', () => {
  it('MessagePosted_ForAnotherChannel_MarksItAndSelectingClearsTheMark', async () => {
    const live = fakeRealtime();
    renderLive(fakeApi(3).api, live.client);
    await openLog();
    const nav = screen.getByRole('navigation');

    live.emit('MessagePosted', { orgId: ORG, channelId: RANDOM.id, message: message(1) });

    const button = await within(nav).findByRole('button', { name: /random/ });
    expect(button).toHaveTextContent('new messages');
    expect(within(nav).getByRole('button', { name: 'general' })).not.toHaveTextContent('new messages');
    fireEvent.click(button);
    await waitFor(() => {
      expect(within(nav).getByRole('button', { name: 'random' })).toBeInTheDocument();
    });
  });

  it('ChannelCreated_AddsTheChannelToTheList', async () => {
    const live = fakeRealtime();
    renderLive(fakeApi(3).api, live.client);
    await openLog();

    live.emit('ChannelCreated', {
      orgId: ORG,
      channel: { id: 'c-new', name: 'design', createdAt: '2026-11-22T10:00:00Z' },
    });

    expect(await screen.findByRole('button', { name: 'design' })).toBeInTheDocument();
  });

  it('SessionRevoked_SentTwice_SignsOutOnce', async () => {
    const live = fakeRealtime();
    const { signOut } = renderLive(fakeApi(3).api, live.client);
    await openLog();

    live.emit('SessionRevoked', { reason: 'logout' });
    live.emit('SessionRevoked', { reason: 'logout' });

    await waitFor(() => {
      expect(signOut).toHaveBeenCalledTimes(1);
    });
  });

  it('Select_ACachedChannel_ShowsWhatItMissedWhileNotOpen', async () => {
    const live = fakeRealtime();
    const { api, grow } = fakeApi(3);
    renderLive(api, live.client);
    await openLog();
    const nav = screen.getByRole('navigation');
    fireEvent.click(within(nav).getByRole('button', { name: 'random' }));
    await screen.findByRole('feed', { name: 'Messages in #random' });
    await screen.findByText('message 3');
    fireEvent.click(within(nav).getByRole('button', { name: 'general' }));
    await screen.findByRole('feed', { name: 'Messages in #general' });

    // A live event reaches the cached channel; another one is dropped.
    live.emit('MessagePosted', { orgId: ORG, channelId: RANDOM.id, message: message(4) });
    grow(5);
    fireEvent.click(within(nav).getByRole('button', { name: /random/ }));
    const log = await screen.findByRole('feed', { name: 'Messages in #random' });

    await waitFor(() => {
      expect(texts(log)).toEqual(['message 1', 'message 2', 'message 3', 'message 4', 'message 5']);
    });
  });

  it('SessionRevoked_SignsOut', async () => {
    const live = fakeRealtime();
    const { signOut } = renderLive(fakeApi(3).api, live.client);
    await openLog();

    live.emit('SessionRevoked', { reason: 'logout' });

    await waitFor(() => {
      expect(signOut).toHaveBeenCalled();
    });
  });

  it('Status_WhileReconnecting_ShowsAPoliteNoticeAfterADelay', async () => {
    const live = fakeRealtime();
    renderLive(fakeApi(3).api, live.client);
    await openLog();

    live.setStatus('reconnecting');

    expect(
      await screen.findByText('Connection lost. Reconnecting…', {}, { timeout: 3000 }),
    ).toBeInTheDocument();
    live.setStatus('connected', true);
    await waitFor(() => {
      expect(screen.queryByText('Connection lost. Reconnecting…')).not.toBeInTheDocument();
    });
  });
});

describe('applyLiveMessage', () => {
  it('Confirmation_AfterALiveMessageWithAHigherSeq_StaysInSeqOrder', () => {
    forgetSeqs();
    const queryClient = new QueryClient();
    queryClient.setQueryData<MessagePages>(messagesKey(ORG, GENERAL.id), {
      pages: [{ messages: [message(2), message(1)], next: null }],
      pageParams: [undefined],
    });

    applyLiveMessage(queryClient, ORG, GENERAL.id, message(4));
    // The person's own message (seq 3) is confirmed after someone else's seq 4 arrived live.
    applyLiveMessage(queryClient, ORG, GENERAL.id, message(3, { authorAccountId: OWN_ID }));

    const data = queryClient.getQueryData<MessagePages>(messagesKey(ORG, GENERAL.id));
    expect(data?.pages[0]?.messages.map(({ seq }) => seq)).toEqual([4, 3, 2, 1]);
  });
});

describe('catchUpChannel', () => {
  const seeded = (newest: number) => {
    forgetSeqs();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const messages = Array.from({ length: newest }, (_, index) => message(newest - index));
    queryClient.setQueryData<MessagePages>(messagesKey(ORG, GENERAL.id), {
      pages: [{ messages, next: null }],
      pageParams: [undefined],
    });
    rememberSeq(ORG, GENERAL.id, newest);
    return queryClient;
  };

  it('CatchUp_WithMoreThanTheBound_ResetsToTheNewestPage', async () => {
    const queryClient = seeded(2);
    let top = 2;
    const calls: MessageCursor[] = [];
    const api = {
      listMessages: vi.fn((_o: string, _c: string, cursor: MessageCursor) => {
        calls.push(cursor);
        if (cursor && 'after' in cursor) {
          top += 1;
          return Promise.resolve({ messages: [message(top)], next: top });
        }
        return Promise.resolve({ messages: [message(top)], next: null });
      }),
    } as unknown as MessagingApi;

    await catchUpChannel(api, queryClient, ORG, GENERAL.id);

    const forward = calls.filter((cursor) => cursor && 'after' in cursor);
    expect(forward).toHaveLength(MAX_CATCH_UP_PAGES);
    // After the bound the channel is reset (an open channel then reads its newest page again).
    expect(queryClient.getQueryData(messagesKey(ORG, GENERAL.id))).toBeUndefined();
  });

  it('CatchUp_WhenThePagesEnd_StopsAndKeepsOrder', async () => {
    const queryClient = seeded(2);
    const api = {
      listMessages: vi.fn(() => Promise.resolve({ messages: [message(4), message(3)], next: null })),
    } as unknown as MessagingApi;

    await catchUpChannel(api, queryClient, ORG, GENERAL.id);

    const data = queryClient.getQueryData<MessagePages>(messagesKey(ORG, GENERAL.id));
    expect(data?.pages[0]?.messages.map(({ seq }) => seq)).toEqual([4, 3, 2, 1]);
    expect(api.listMessages).toHaveBeenCalledTimes(1);
  });

  it('CatchUp_WhenTheReadFails_KeepsTheCacheAndReturnsFalse', async () => {
    const queryClient = seeded(2);
    const api = {
      listMessages: vi.fn(() => Promise.reject(new Error('offline'))),
    } as unknown as MessagingApi;

    const ok = await catchUpChannel(api, queryClient, ORG, GENERAL.id);

    expect(ok).toBe(false);
    expect(
      queryClient.getQueryData<MessagePages>(messagesKey(ORG, GENERAL.id))?.pages[0]?.messages,
    ).toHaveLength(2);
  });
});

describe('Live connection lifecycle', () => {
  it('Workspace_InStrictMode_OpensOneHubConnectionAndStopsItOnUnmount', async () => {
    forgetSeqs();
    const hubs: { start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn> }[] = [];
    const factory = (cell: { id: string; url: string }) =>
      createRealtimeClient({
        cellUrl: cell.url,
        getAccessToken: () => Promise.resolve('token'),
        // The real factory loads the library first, so the hub arrives a tick later.
        hubFactory: () =>
          new Promise<Hub>((resolve) => {
            setTimeout(() => {
              const hub = {
                start: vi.fn(() => Promise.resolve()),
                stop: vi.fn(() => Promise.resolve()),
                invoke: vi.fn(() => Promise.resolve(undefined)),
                on: vi.fn(),
                onClose: vi.fn(),
              };
              hubs.push(hub);
              resolve(hub);
            }, 0);
          }),
      });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const view = render(
      <StrictMode>
        <LocaleProvider locale="en">
          <QueryClientProvider client={queryClient}>
            <AuthProvider client={fakeAuthClient()}>
              <MessagingApiContext.Provider value={() => fakeApi(3).api}>
                <RealtimeContext.Provider value={factory}>
                  <Workspace
                    organization={{
                      id: ORG,
                      name: 'Acme',
                      cell: { id: 'a', url: 'https://cell.example.test' },
                    }}
                  />
                </RealtimeContext.Provider>
              </MessagingApiContext.Provider>
            </AuthProvider>
          </QueryClientProvider>
        </LocaleProvider>
      </StrictMode>,
    );
    await openLog();
    await waitFor(() => {
      expect(hubs.length).toBeGreaterThan(0);
    });
    await new Promise((resolve) => setTimeout(resolve, 20));

    // A hub that was built but never started opens no socket.
    const connected = hubs.filter((hub) => hub.start.mock.calls.length > 0);
    expect(connected).toHaveLength(1);

    view.unmount();
    await waitFor(() => {
      expect(connected[0]?.stop).toHaveBeenCalled();
    });
  });
});
