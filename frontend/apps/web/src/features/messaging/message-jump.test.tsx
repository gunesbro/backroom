import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionStatus, RealtimeClient, RealtimeEventName, RealtimeEvents } from '@backroom/realtime';
import { AuthProvider } from '@/features/auth';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { createMessagingApi, MessagingApiContext } from './api';
import { QuoteLine } from './message-quote';
import { RealtimeContext } from './realtime-context';
import { messagesKey, type MessagePages } from './queries';
import { forgetSeqs } from './seq-memory';
import { Workspace } from './workspace';

// Jumping to a quoted original that is not loaded (docs/contracts/phase-2.md, "Jump to a message"): the real API
// client against a cell served by MSW.

const flags = vi.hoisted(() => ({ reply: true }));

vi.mock('@/config', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/config')>();
  return {
    config: {
      ...original.config,
      get messageReplyEnabled() {
        return flags.reply;
      },
    },
  };
});

const CELL = 'https://cell.example.test';
const ORG = '0b6f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a11';
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const OTHER_ID = '00000000-0000-4000-8000-000000000002';
const GENERAL = { id: 'c-general', name: 'general', createdAt: '2026-11-20T09:00:00Z' };
const RANDOM = { id: 'c-random', name: 'random', createdAt: '2026-11-20T09:05:00Z' };
const EPOCH = 'e1';
const QUOTE_NAME = 'Replied message: Veli Yılmaz: message 100';

type Wire = {
  id: string;
  seq: number;
  clientMessageId: string | null;
  authorAccountId: string | null;
  text: string | null;
  createdAt: string;
  deletedAt?: string;
  replyTo?: { messageId: string; deleted: boolean; authorAccountId: string | null; text: string | null };
};

const message = (seq: number): Wire => ({
  id: `m-${String(seq)}`,
  seq,
  clientMessageId: `00000000-0000-4000-8000-${String(seq).padStart(12, '0')}`,
  authorAccountId: OTHER_ID,
  text: `message ${String(seq)}`,
  createdAt: '2026-11-22T10:00:00Z',
});

/** The channel: messages 1..count; the newest replies to message 100, which the newest page (50) does not hold. */
type Cell = {
  count: number;
  /** Seqs that are tombstones. */
  deleted: Set<number>;
  /** What the around read answers instead of the messages, if anything. */
  aroundStatus?: { status: number; code: string };
  /** Held until this settles: the answer to the next around read (its body is made when the request comes). */
  holdAround?: Promise<void> | undefined;
  /** Held until this settles: the answer to the next after read. */
  holdAfter?: Promise<void> | undefined;
  /** Held until this settles: the answer to the next read without a cursor or with `before`. */
  holdBefore?: Promise<void> | undefined;
  /** What a read with an epoch answers instead of the messages, if anything. */
  epochStatus?: { status: number; code: string };
};

let cell: Cell;
let requested: URL[] = [];
const server = setupServer();

function wire(seq: number): Wire {
  if (cell.deleted.has(seq)) {
    return {
      id: `m-${String(seq)}`,
      seq,
      clientMessageId: null,
      authorAccountId: null,
      text: null,
      createdAt: '2026-11-22T10:00:00Z',
      deletedAt: '2026-11-22T11:00:00Z',
    };
  }

  const base = message(seq);
  // Message 105 (in the window around 100) replies to message 30, outside that window.
  // Message 155 (in the newest page) replies to message 50, which it does not hold; message 110 (in the window around
  // 100) replies to message 150, whose own window reaches the newest message.
  if (seq === 155) {
    return {
      ...base,
      replyTo: { messageId: 'm-50', deleted: false, authorAccountId: OTHER_ID, text: 'message 50' },
    };
  }
  if (seq === 110) {
    return {
      ...base,
      replyTo: { messageId: 'm-150', deleted: false, authorAccountId: OTHER_ID, text: 'message 150' },
    };
  }
  if (seq === 105) {
    return {
      ...base,
      replyTo: { messageId: 'm-30', deleted: false, authorAccountId: OTHER_ID, text: 'message 30' },
    };
  }
  return seq === cell.count
    ? {
        ...base,
        replyTo: { messageId: 'm-100', deleted: false, authorAccountId: OTHER_ID, text: 'message 100' },
      }
    : base;
}

const problem = ({ status, code }: { status: number; code: string }) =>
  HttpResponse.json(
    { status, code, title: code },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );

function serveCell() {
  server.use(
    http.get(`${CELL}/api/v1/orgs/${ORG}/members`, () =>
      HttpResponse.json({
        members: [
          { accountId: OWN_ID, displayName: 'Alice Example', email: 'alice@example.test' },
          { accountId: OTHER_ID, displayName: 'Veli Yılmaz', email: null },
        ],
        next: null,
      }),
    ),
    http.get(`${CELL}/api/v1/orgs/${ORG}/conversations`, () =>
      HttpResponse.json({ items: [GENERAL, RANDOM], next: null }),
    ),
    http.get(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId`, ({ params }) =>
      HttpResponse.json({
        id: params.channelId,
        kind: 'public_channel',
        name: params.channelId === RANDOM.id ? RANDOM.name : GENERAL.name,
        description: null,
        createdAt: GENERAL.createdAt,
        archivedAt: null,
        memberCount: 2,
        ownerAccountId: null,
        role: 'member',
      }),
    ),
    http.get(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId/messages`, async ({ request, params }) => {
      if (params.channelId === RANDOM.id) {
        return HttpResponse.json({
          items: [{ ...message(1), id: 'r-1', text: 'random message' }],
          next: null,
        });
      }
      const url = new URL(request.url);
      requested.push(url);
      const query = url.searchParams;
      const limit = Number(query.get('limit'));
      const around = query.get('around');
      if (around !== null) {
        if (cell.aroundStatus) return problem(cell.aroundStatus);
        const anchor = Number(around.slice(2));
        if (!(anchor >= 1 && anchor <= cell.count))
          return problem({ status: 404, code: 'messaging.message_not_found' });
        // As the server shares the places: half older, the rest newer.
        const olderWanted = Math.floor((limit - 1) / 2);
        const older = Math.min(anchor - 1, Math.max(olderWanted, limit - 1 - (cell.count - anchor)));
        const newer = Math.min(cell.count - anchor, limit - 1 - older);
        const seqs: number[] = [];
        for (let seq = anchor + newer; seq >= anchor - older; seq -= 1) seqs.push(seq);
        const body = {
          items: seqs.map(wire),
          before: anchor - older > 1 ? anchor - older : null,
          after: anchor + newer < cell.count ? anchor + newer : null,
          anchor: { id: around, seq: anchor },
          epoch: EPOCH,
        };
        const hold = cell.holdAround;
        cell.holdAround = undefined;
        await hold;
        return HttpResponse.json(body);
      }

      if (query.has('epoch') && cell.epochStatus) return problem(cell.epochStatus);
      const after = query.get('after');
      if (after !== null) {
        // Oldest first, `next` forward (null: nothing newer).
        const seqs: number[] = [];
        for (let seq = Number(after) + 1; seq <= cell.count && seqs.length < limit; seq += 1) seqs.push(seq);
        const last = seqs.at(-1);
        const body = {
          items: seqs.map(wire),
          next: last !== undefined && last < cell.count ? last : null,
          epoch: EPOCH,
        };
        const hold = cell.holdAfter;
        cell.holdAfter = undefined;
        await hold;
        return HttpResponse.json(body);
      }

      const before = query.get('before');
      const first = before === null ? cell.count : Number(before) - 1;
      const seqs: number[] = [];
      for (let seq = first; seq >= 1 && seqs.length < limit; seq -= 1) seqs.push(seq);
      const last = seqs.at(-1) ?? 1;
      const body = { items: seqs.map(wire), next: last > 1 ? last : null, epoch: EPOCH };
      const hold = cell.holdBefore;
      cell.holdBefore = undefined;
      await hold;
      return HttpResponse.json(body);
    }),
  );
}

function fakeRealtime() {
  const listeners = new Map<RealtimeEventName, ((payload: never) => void)[]>();
  const statusListeners: ((status: ConnectionStatus, reconnected: boolean) => void)[] = [];
  const client = {
    start: vi.fn(),
    stop: vi.fn(() => Promise.resolve()),
    subscribeOrganization: vi.fn(),
    unsubscribeOrganization: vi.fn(),
    subscribeConversation: vi.fn(),
    unsubscribeConversation: vi.fn(),
    watchPresence: vi.fn(),
    reportActivity: vi.fn(),
    on: (event: RealtimeEventName, listener: (payload: never) => void) => {
      listeners.set(event, [...(listeners.get(event) ?? []), listener]);
      return () => undefined;
    },
    onStatus: (listener: (status: ConnectionStatus, reconnected: boolean) => void) => {
      statusListeners.push(listener);
      return () => undefined;
    },
    status: (): ConnectionStatus => 'connected',
  };
  return {
    client: client as unknown as RealtimeClient,
    /** The connection came back after it was lost. */
    reconnect: () => {
      act(() => {
        for (const listener of statusListeners) listener('connected', true);
      });
    },
    emit: <K extends RealtimeEventName>(event: K, payload: RealtimeEvents[K]) => {
      act(() => {
        for (const listener of listeners.get(event) ?? []) listener(payload as never);
      });
    },
  };
}

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
  // jsdom has no layout: the log is 400 px tall, a row 40 px, and scrollTop can be set.
  const tops = new WeakMap<Element, number>();
  const isLog = (element: Element) => element.getAttribute('role') === 'feed';
  // As a browser does: the position stays inside the content, and a scroll that moves it reports itself with a scroll
  // event a moment later (jsdom sends none), which is how the list's virtualizer learns where it is.
  const moveTo = (element: HTMLElement, value: number) => {
    const max = isLog(element)
      ? Math.max(element.scrollHeight - element.clientHeight, 0)
      : Number.POSITIVE_INFINITY;
    const next = Math.min(Math.max(value, 0), max);
    if (next === (tops.get(element) ?? 0)) return;
    tops.set(element, next);
    setTimeout(() => {
      element.dispatchEvent(new Event('scroll'));
    }, 0);
  };
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
    scrollTo: {
      configurable: true,
      value: function (this: HTMLElement, options: { top?: number }) {
        moveTo(this, options.top ?? 0);
      },
    },
    scrollTop: {
      configurable: true,
      get: function (this: HTMLElement) {
        return tops.get(this) ?? 0;
      },
      set: function (this: HTMLElement, value: number) {
        moveTo(this, value);
      },
    },
  });
});

beforeEach(() => {
  flags.reply = true;
  cell = { count: 160, deleted: new Set() };
  serveCell();
});

afterEach(() => {
  server.resetHandlers();
  requested = [];
  forgetSeqs();
});

afterAll(() => {
  server.close();
});

function renderWorkspace(realtime: RealtimeClient = fakeRealtime().client) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <LocaleProvider locale="en">
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={fakeAuthClient()}>
          <MessagingApiContext.Provider
            value={(current) => createMessagingApi(current.url, () => Promise.resolve('cell-token'))}
          >
            <RealtimeContext.Provider value={() => realtime}>
              <Workspace organization={{ id: ORG, name: 'Acme', cell: { id: 'a', url: CELL } }} />
            </RealtimeContext.Provider>
          </MessagingApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>,
  );
  return queryClient;
}

async function openLog() {
  const log = await screen.findByRole('feed', { name: 'Messages in #general' });
  await waitFor(() => {
    expect(within(log).queryAllByRole('article').length).toBeGreaterThan(0);
  });
  fireEvent.scroll(log);
  await within(log).findByText(`message ${String(cell.count)}`);
  return log;
}

const rowOf = (log: HTMLElement, text: string) =>
  within(log)
    .getAllByText(text)
    .find((element) => !element.closest('.bm-reply-quote'))
    ?.closest<HTMLElement>('[role="article"]') as HTMLElement;

const reads = (name: string) => requested.filter((url) => url.searchParams.has(name));

/** Finds a row's text once the log has drawn it: jsdom sends no scroll event when the list sets its position. */
async function findInLog(log: HTMLElement, text: string) {
  return waitFor(() => {
    fireEvent.scroll(log);
    return within(log).getByText(text);
  });
}

/** Rows (not quotes) that say this text. */
const rowsSaying = (log: HTMLElement, text: string) =>
  within(log)
    .queryAllByText(text)
    .filter((element) => !element.closest('.bm-reply-quote'));

/** Lets what a request or an event started finish, so a negative check cannot pass just because it ran early. */
const settle = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

function deferred() {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

const latestButton = () => screen.queryByRole('button', { name: 'Jump to latest' });

/** Opens the channel and jumps to message 100 from the quote in message 160. */
async function jumpToOriginal(keyboard = false) {
  const log = await openLog();
  fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }), keyboard ? { detail: 0 } : {});
  await waitFor(() => {
    expect(rowOf(log, 'message 100')).toHaveFocus();
  });
  return log;
}

/** Scrolls the log to its top or its end, as the person would. */
function scrollLog(log: HTMLElement, to: 'top' | 'end') {
  act(() => {
    log.scrollTop = to === 'top' ? 0 : log.scrollHeight;
  });
  fireEvent.scroll(log);
}

describe('Jump to an original that is not loaded', () => {
  it('Jump_ToAnOriginalNotLoaded_ShowsTheWindowAroundItAndFocusesAndMarksIt', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();

    const around = reads('around');
    expect(around).toHaveLength(1);
    expect(around[0]?.searchParams.get('around')).toBe('m-100');
    expect(around[0]?.searchParams.get('limit')).toBe('50');
    // It takes no cursor, so no epoch.
    expect(around[0]?.searchParams.has('epoch')).toBe(false);
    expect(rowOf(log, 'message 100')).toHaveClass('bm-message--flash');
    // The view is the window (76..125): the newest messages are gone from it.
    expect(within(log).queryByText('message 160')).toBeNull();
    expect(latestButton()).toBeVisible();
  });

  it('Jump_WithTheKeyboard_AlsoMovesTheFocus', async () => {
    renderWorkspace();
    const log = await jumpToOriginal(true);

    expect(rowOf(log, 'message 100')).toHaveClass('bm-message--flash');
  });

  it('JumpToLatest_ReturnsToTheNewestMessagesAndFocusesTheNewest', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();
    const button = latestButton() as HTMLElement;

    button.focus();
    fireEvent.click(button);

    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    expect(
      within(log)
        .queryAllByText('message 100')
        .filter((text) => !text.closest('.bm-reply-quote')),
    ).toHaveLength(0);
    await findInLog(log, 'message 160');
    await waitFor(() => {
      expect(rowOf(log, 'message 160')).toHaveFocus();
    });
    // Following again: the log is at its end.
    expect(log.scrollTop + log.clientHeight).toBeGreaterThanOrEqual(log.scrollHeight);
  });

  it('JumpToLatest_IsReachedWithTheKeyboardAfterTheLog', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();
    const button = latestButton() as HTMLElement;

    // After the log in the page (tab order), before the composer.
    expect(log.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(button.tagName).toBe('BUTTON');
  });

  it('Scrolling_TheWindow_LoadsOlderAndNewerPagesWithItsCursorsAndEpoch', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();

    scrollLog(log, 'top');
    await within(log).findByText('message 75');
    const before = reads('before').find((url) => url.searchParams.get('before') === '76');
    expect(before).toBeDefined();
    // Each earlier page is asked for once: no cursor is read twice.
    const cursors = reads('before').map((url) => url.searchParams.get('before'));
    expect(new Set(cursors).size).toBe(cursors.length);
    expect(before?.searchParams.get('epoch')).toBe(EPOCH);

    scrollLog(log, 'end');
    await within(log).findByText('message 126');
    const after = reads('after').find((url) => url.searchParams.get('after') === '125');
    expect(after).toBeDefined();
    expect(after?.searchParams.get('epoch')).toBe(EPOCH);
  });

  it('Scrolling_ToTheNewestMessage_AttachesTheViewAgain', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(realtime.client);
    const log = await jumpToOriginal();

    scrollLog(log, 'end');
    await waitFor(() => {
      expect(reads('after')).toHaveLength(1);
    });

    // The read after 125 reached the newest message: the button goes; one read after the newest fills the gap.
    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    await waitFor(() => {
      expect(reads('after').map((url) => url.searchParams.get('after'))).toEqual(['125', '160']);
    });

    // Attached again: a live message joins the list as usual.
    realtime.emit('MessagePosted', {
      orgId: ORG,
      channelId: GENERAL.id,
      message: {
        id: 'm-161',
        seq: 161,
        clientMessageId: '00000000-0000-4000-8000-000000000161',
        authorAccountId: OTHER_ID,
        text: 'message 161',
        createdAt: '2026-11-22T10:00:00Z',
      },
    });
    scrollLog(log, 'end');
    expect(await findInLog(log, 'message 161')).toBeInTheDocument();
  });

  it('LiveMessage_WhileDetached_IsNotDrawnAndIsThereAfterReturning', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(realtime.client);
    const log = await jumpToOriginal();

    cell.count = 161;
    realtime.emit('MessagePosted', {
      orgId: ORG,
      channelId: GENERAL.id,
      message: {
        id: 'm-161',
        seq: 161,
        clientMessageId: '00000000-0000-4000-8000-000000000161',
        authorAccountId: OTHER_ID,
        text: 'message 161',
        createdAt: '2026-11-22T10:00:00Z',
      },
    });
    // The window does not move and does not show it.
    expect(within(log).queryByText('message 161')).toBeNull();
    expect(rowOf(log, 'message 100')).toHaveFocus();

    fireEvent.click(latestButton() as HTMLElement);

    expect(await findInLog(log, 'message 161')).toBeInTheDocument();
    // What came with going back is shown, not announced as news.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1600));
    });
    expect(document.querySelector('.bm-sr-only[role="status"]')?.textContent ?? '').not.toContain(
      'message 161',
    );
  });

  it('Jump_ToAnOriginalThatCannotBeRead_ShowsANoticeAndStaysPut', async () => {
    cell.aroundStatus = { status: 404, code: 'messaging.message_not_found' };
    renderWorkspace();
    const log = await openLog();

    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));

    expect(await screen.findByText('This message can no longer be shown.')).toBeInTheDocument();
    expect(within(log).getByText('message 160')).toBeInTheDocument();
    expect(latestButton()).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.queryByText('This message can no longer be shown.')).toBeNull();
  });

  it('Jump_ToAnOriginalThatIsATombstone_GoesToItsPlace', async () => {
    cell.deleted.add(100);
    renderWorkspace();
    const log = await openLog();
    // The quote is a snapshot: the newest page does not know the original was deleted.
    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));

    await waitFor(() => {
      expect(document.activeElement).toHaveClass('bm-message--deleted');
    });
    expect(document.activeElement).toHaveClass('bm-message--flash');
    expect(document.activeElement).toHaveTextContent('This message was deleted');
  });

  it('ResyncRequired_WhileReadingTheWindow_StartsOverFromTheNewestMessages', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();
    cell.epochStatus = { status: 409, code: 'messaging.resync_required' };

    scrollLog(log, 'top');

    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    await settle();
    expect(await findInLog(log, 'message 160')).toBeInTheDocument();
  });

  it('Send_WhileDetached_ReturnsToTheNewestMessages', async () => {
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId/messages`, async ({ request }) => {
        const body = (await request.json()) as { clientMessageId: string; text: string };
        cell.count = 161;
        return HttpResponse.json(
          {
            ...message(161),
            clientMessageId: body.clientMessageId,
            authorAccountId: OWN_ID,
            text: body.text,
          },
          { status: 201 },
        );
      }),
    );
    renderWorkspace();
    const log = await jumpToOriginal();
    const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Message #general' });

    // The person moves to the composer and writes there.
    act(() => {
      field.focus();
    });
    fireEvent.change(field, { target: { value: 'hello' } });
    fireEvent.keyDown(field, { key: 'Enter' });

    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    expect(await findInLog(log, 'message 160')).toBeInTheDocument();
  });

  it('ChannelSwitch_AwayAndBack_OpensAtTheNewestMessages', async () => {
    renderWorkspace();
    await jumpToOriginal();

    fireEvent.click(screen.getByRole('button', { name: 'random' }));
    const random = await screen.findByRole('feed', { name: 'Messages in #random' });
    await within(random).findByText('random message');
    fireEvent.click(screen.getByRole('button', { name: 'general' }));

    const log = await openLog();
    expect(
      within(log)
        .queryAllByText('message 100')
        .filter((text) => !text.closest('.bm-reply-quote')),
    ).toHaveLength(0);
    expect(latestButton()).toBeNull();
    // The newest messages were read again, not drawn from the dropped window.
    expect(requested.filter((url) => url.search === '?limit=50')).toHaveLength(2);
  });

  it('Jump_AnsweredAfterTheChannelWasLeft_DoesNotBringTheWindowBack', async () => {
    const hold = deferred();
    cell.holdAround = hold.promise;
    renderWorkspace();
    const log = await openLog();
    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(reads('around')).toHaveLength(1);
    });

    fireEvent.click(screen.getByRole('button', { name: 'random' }));
    const random = await screen.findByRole('feed', { name: 'Messages in #random' });
    await within(random).findByText('random message');
    hold.resolve();
    await settle();
    fireEvent.click(screen.getByRole('button', { name: 'general' }));

    const again = await openLog();
    expect(latestButton()).toBeNull();
    expect(rowsSaying(again, 'message 100')).toHaveLength(0);
  });

  it('Jump_AnsweredAfterJumpToLatest_DoesNotReplaceTheNewestMessages', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();
    const hold = deferred();
    cell.holdAround = hold.promise;
    // A second jump from the window (message 105 quotes message 30), still on its way when the person goes back.
    fireEvent.click(within(log).getByRole('button', { name: 'Replied message: Veli Yılmaz: message 30' }));
    await waitFor(() => {
      expect(reads('around')).toHaveLength(2);
    });

    fireEvent.click(latestButton() as HTMLElement);
    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    hold.resolve();
    await settle();

    expect(latestButton()).toBeNull();
    expect(rowsSaying(log, 'message 30')).toHaveLength(0);
    expect(await findInLog(log, 'message 160')).toBeInTheDocument();
  });

  it('Jump_AnsweredAfterASend_DoesNotReplaceTheNewestMessages', async () => {
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId/messages`, async ({ request }) => {
        const body = (await request.json()) as { clientMessageId: string; text: string };
        return HttpResponse.json(
          {
            ...message(161),
            clientMessageId: body.clientMessageId,
            authorAccountId: OWN_ID,
            text: body.text,
          },
          { status: 201 },
        );
      }),
    );
    const hold = deferred();
    cell.holdAround = hold.promise;
    renderWorkspace();
    const log = await openLog();
    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(reads('around')).toHaveLength(1);
    });

    const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Message #general' });
    act(() => {
      field.focus();
    });
    fireEvent.change(field, { target: { value: 'hello' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    hold.resolve();
    await settle();

    expect(latestButton()).toBeNull();
    expect(rowsSaying(log, 'message 100')).toHaveLength(0);
    expect(within(log).getByText('message 160')).toBeInTheDocument();
  });

  it('Attach_WhenALiveMessageCameDuringTheAttachingRead_ReadsItAfterwards', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(realtime.client);
    const log = await jumpToOriginal();
    const hold = deferred();
    cell.holdAfter = hold.promise;

    scrollLog(log, 'end');
    await waitFor(() => {
      expect(reads('after')).toHaveLength(1);
    });
    // Posted while the read that attaches the view is on its way: the detached view does not take it.
    cell.count = 161;
    realtime.emit('MessagePosted', {
      orgId: ORG,
      channelId: GENERAL.id,
      message: {
        id: 'm-161',
        seq: 161,
        clientMessageId: '00000000-0000-4000-8000-000000000161',
        authorAccountId: OTHER_ID,
        text: 'message 161',
        createdAt: '2026-11-22T10:00:00Z',
      },
    });
    hold.resolve();

    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    await waitFor(() => {
      expect(reads('after').map((url) => url.searchParams.get('after'))).toEqual(['125', '160']);
    });
    scrollLog(log, 'end');
    expect(await findInLog(log, 'message 161')).toBeInTheDocument();
  });

  it('Jump_WhenTheReadFails_ShowsTheGenericNotice', async () => {
    cell.aroundStatus = { status: 500, code: 'server.unexpected' };
    renderWorkspace();
    const log = await openLog();

    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));

    expect(await screen.findByText('Something went wrong. Try again.')).toBeInTheDocument();
    expect(latestButton()).toBeNull();
  });

  it('JumpNotice_IsALiveRegionInThePageBeforeItHasText', async () => {
    cell.aroundStatus = { status: 404, code: 'messaging.message_not_found' };
    renderWorkspace();
    const log = await openLog();
    const region = document.querySelector('.bm-jump-notice[role="status"]');
    expect(region).toBeEmptyDOMElement();

    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));

    await waitFor(() => {
      expect(region).toHaveTextContent('This message can no longer be shown.');
    });
    expect(document.querySelector('.bm-jump-notice[role="status"]')).toBe(region);
  });

  it('ReplyTarget_WhileDetachedWithoutItsOriginal_KeepsItsBarAndIsSent', async () => {
    const sent: { replyToMessageId?: string }[] = [];
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId/messages`, async ({ request }) => {
        const body = (await request.json()) as {
          clientMessageId: string;
          text: string;
          replyToMessageId?: string;
        };
        sent.push(body);
        return HttpResponse.json(
          {
            ...message(161),
            clientMessageId: body.clientMessageId,
            authorAccountId: OWN_ID,
            text: body.text,
          },
          { status: 201 },
        );
      }),
    );
    renderWorkspace();
    const log = await openLog();
    const reply = rowOf(log, 'message 159');
    fireEvent.pointerEnter(reply, { pointerType: 'mouse' });
    fireEvent.click(within(reply).getByRole('button', { name: 'Reply' }));
    expect(document.querySelector('.bm-replybar')).toHaveTextContent('Veli Yılmaz');

    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(rowOf(log, 'message 100')).toHaveFocus();
    });

    // Message 159 is not in the window: the bar stays, with the name and no excerpt.
    const bar = document.querySelector('.bm-replybar');
    expect(bar).toHaveTextContent('Veli Yılmaz');
    expect(bar?.querySelector('.bm-replybar__text')).toBeNull();

    const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Message #general' });
    act(() => {
      field.focus();
    });
    fireEvent.change(field, { target: { value: 'hello' } });
    fireEvent.keyDown(field, { key: 'Enter' });

    await waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]?.replyToMessageId).toBe('m-159');
  });

  it('Refetch_OfAWindowWithNewerPagesAboveIt_KeepsOneContiguousRun', async () => {
    cell.count = 300;
    const queryClient = renderWorkspace();
    const log = await jumpToOriginal();
    scrollLog(log, 'end');
    await within(log).findByText('message 126');

    requested = [];
    await act(async () => {
      await queryClient.refetchQueries({ queryKey: messagesKey(ORG, GENERAL.id) });
    });

    // Rebuilt from its first page (after 125), then the page below it, both with the window's epoch.
    expect(requested.map((url) => url.search)).toEqual([
      '?limit=50&after=125&epoch=e1',
      '?limit=50&before=126&epoch=e1',
    ]);
    const data = queryClient.getQueryData<MessagePages>(messagesKey(ORG, GENERAL.id));
    const seqs = data?.pages.flatMap((page) => page.messages.map(({ seq }) => seq)) ?? [];
    expect(seqs[0]).toBe(175);
    expect(seqs.at(-1)).toBe(76);
    seqs.slice(1).forEach((seq, index) => {
      expect(seq).toBe((seqs[index] ?? 0) - 1);
    });
    expect(latestButton()).toBeVisible();
  });

  it('Reconnect_WhileDetached_ReadsTheWindowAgainWhereItIs', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(realtime.client);
    const log = await jumpToOriginal();

    realtime.reconnect();

    await waitFor(() => {
      expect(reads('around')).toHaveLength(2);
    });
    expect(latestButton()).toBeVisible();
    expect(rowsSaying(log, 'message 100')).toHaveLength(1);
    expect(requested.filter((url) => url.search === '?limit=50')).toHaveLength(1);
  });

  it('ReplyTarget_WhenItsOriginalIsDeletedWhileDetached_IsDroppedAndTheSendIsNoReply', async () => {
    const sent: { replyToMessageId?: string }[] = [];
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId/messages`, async ({ request }) => {
        const body = (await request.json()) as {
          clientMessageId: string;
          text: string;
          replyToMessageId?: string;
        };
        sent.push(body);
        return HttpResponse.json(
          {
            ...message(161),
            clientMessageId: body.clientMessageId,
            authorAccountId: OWN_ID,
            text: body.text,
          },
          { status: 201 },
        );
      }),
    );
    const realtime = fakeRealtime();
    renderWorkspace(realtime.client);
    const log = await openLog();
    const reply = rowOf(log, 'message 159');
    fireEvent.pointerEnter(reply, { pointerType: 'mouse' });
    fireEvent.click(within(reply).getByRole('button', { name: 'Reply' }));
    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(rowOf(log, 'message 100')).toHaveFocus();
    });
    expect(document.querySelector('.bm-replybar')).toHaveTextContent('Veli Yılmaz');

    // Deleted while the window does not hold it.
    realtime.emit('MessageDeleted', {
      orgId: ORG,
      conversationId: GENERAL.id,
      messageId: 'm-159',
      versionSeq: 500,
      changeSeq: 500,
      epoch: EPOCH,
    });

    expect(document.querySelector('.bm-replybar')).toBeNull();
    const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Message #general' });
    act(() => {
      field.focus();
    });
    fireEvent.change(field, { target: { value: 'hello' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    await waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]?.replyToMessageId).toBeUndefined();
    expect(document.querySelector('.bm-replybar')).toBeNull();
  });

  it('Jump_OvertakenByASecondJump_ShowsTheSecond', async () => {
    const hold = deferred();
    cell.holdAround = hold.promise;
    renderWorkspace();
    const log = await openLog();
    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(reads('around')).toHaveLength(1);
    });

    // Message 155 quotes message 50: the second jump is answered first.
    fireEvent.click(within(log).getByRole('button', { name: 'Replied message: Veli Yılmaz: message 50' }));
    await waitFor(() => {
      expect(rowOf(log, 'message 50')).toHaveFocus();
    });
    hold.resolve();
    await settle();

    expect(rowOf(log, 'message 50')).toHaveFocus();
    expect(rowsSaying(log, 'message 100')).toHaveLength(0);
    expect(latestButton()).toBeVisible();
  });

  it('Jump_WithNothingNewer_AttachesAndFillsTheGapBehindItsAnswer', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(realtime.client);
    const log = await jumpToOriginal();
    const hold = deferred();
    cell.holdAround = hold.promise;
    fireEvent.click(within(log).getByRole('button', { name: 'Replied message: Veli Yılmaz: message 150' }));
    await waitFor(() => {
      expect(reads('around')).toHaveLength(2);
    });
    // Posted after the answer was made: the answer that replaces the view does not hold it.
    cell.count = 161;
    realtime.emit('MessagePosted', {
      orgId: ORG,
      channelId: GENERAL.id,
      message: {
        id: 'm-161',
        seq: 161,
        clientMessageId: '00000000-0000-4000-8000-000000000161',
        authorAccountId: OTHER_ID,
        text: 'message 161',
        createdAt: '2026-11-22T10:00:00Z',
      },
    });
    hold.resolve();

    await waitFor(() => {
      expect(reads('after').map((url) => url.searchParams.get('after'))).toEqual(['160']);
    });
    expect(latestButton()).toBeNull();
    scrollLog(log, 'end');
    expect(await findInLog(log, 'message 161')).toBeInTheDocument();
  });

  it('Attach_WhenMoreThanAPageCameMeanwhile_ReloadsTheNewestMessages', async () => {
    renderWorkspace();
    const log = await jumpToOriginal();
    const hold = deferred();
    cell.holdAfter = hold.promise;

    scrollLog(log, 'end');
    await waitFor(() => {
      expect(reads('after')).toHaveLength(1);
    });
    // The read that attaches answers up to 160; 100 more come before the gap is filled.
    cell.count = 260;
    hold.resolve();

    await waitFor(() => {
      expect(requested.filter((url) => url.search === '?limit=50')).toHaveLength(2);
    });
    expect(reads('after').map((url) => url.searchParams.get('after'))).toEqual(['125', '160']);
    expect(latestButton()).toBeNull();
    // The reload draws the list anew.
    const fresh = await screen.findByRole('feed', { name: 'Messages in #general' });
    scrollLog(fresh, 'end');
    expect(await findInLog(fresh, 'message 260')).toBeInTheDocument();
  });

  it('QuoteLine_WithoutText_ShowsTheNameOnly', () => {
    render(
      <LocaleProvider locale="en">
        <QuoteLine
          quote={{ messageId: 'm-1', deleted: false, authorAccountId: OTHER_ID, text: '' }}
          name="Veli Yılmaz"
          authorId={OTHER_ID}
          canJump
          onJump={() => undefined}
        />
      </LocaleProvider>,
    );

    const quote = screen.getByRole('button', { name: 'Replied message: Veli Yılmaz' });
    expect(quote.querySelector('.bm-reply-quote__text')).toBeNull();
  });

  it('Jump_WhileARefetchOfTheNewestMessagesIsOnItsWay_KeepsTheWindow', async () => {
    const queryClient = renderWorkspace();
    const log = await openLog();
    const hold = deferred();
    cell.holdBefore = hold.promise;
    // A refetch of the newest view (focus, reconnect, an invalidation) is still on its way when the person jumps.
    void queryClient.refetchQueries({ queryKey: messagesKey(ORG, GENERAL.id) });
    await waitFor(() => {
      expect(requested.filter((url) => url.search === '?limit=50')).toHaveLength(2);
    });

    fireEvent.click(within(log).getByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(rowOf(log, 'message 100')).toHaveFocus();
    });
    hold.resolve();
    await settle();

    expect(rowsSaying(log, 'message 100')).toHaveLength(1);
    expect(latestButton()).toBeVisible();
  });

  it('Jump_WhileEarlierMessagesOfTheNewestViewAreLoading_KeepsTheWindow', async () => {
    renderWorkspace();
    const log = await openLog();
    const hold = deferred();
    cell.holdBefore = hold.promise;
    scrollLog(log, 'top');
    await waitFor(() => {
      expect(reads('before')).toHaveLength(1);
    });
    scrollLog(log, 'end');

    fireEvent.click(await within(log).findByRole('button', { name: QUOTE_NAME }));
    await waitFor(() => {
      expect(rowOf(log, 'message 100')).toHaveFocus();
    });
    hold.resolve();
    await settle();

    expect(rowsSaying(log, 'message 100')).toHaveLength(1);
    expect(latestButton()).toBeVisible();
  });

  it('Quote_WhenTheFlagIsOff_IsTextAndNothingIsRead', async () => {
    flags.reply = false;
    renderWorkspace();
    const log = await openLog();

    expect(within(log).queryByRole('button', { name: QUOTE_NAME })).toBeNull();
    expect(reads('around')).toHaveLength(0);
  });

  it('Detached_HasNoAccessibilityViolations', async () => {
    renderWorkspace();
    await jumpToOriginal();

    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false }, region: { enabled: false } },
    });
    expect(results.violations).toEqual([]);
  });
});

describe('Jump to an original in Turkish', () => {
  it('JumpToLatest_InTurkish_UsesTheApprovedText', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <LocaleProvider locale="tr">
        <QueryClientProvider client={queryClient}>
          <AuthProvider client={fakeAuthClient()}>
            <MessagingApiContext.Provider
              value={(current) => createMessagingApi(current.url, () => Promise.resolve('cell-token'))}
            >
              <RealtimeContext.Provider value={() => fakeRealtime().client}>
                <Workspace organization={{ id: ORG, name: 'Acme', cell: { id: 'a', url: CELL } }} />
              </RealtimeContext.Provider>
            </MessagingApiContext.Provider>
          </AuthProvider>
        </QueryClientProvider>
      </LocaleProvider>,
    );
    const log = await screen.findByRole('feed');
    await waitFor(() => {
      expect(within(log).queryAllByRole('article').length).toBeGreaterThan(0);
    });
    fireEvent.scroll(log);
    await within(log).findByText('message 160');

    fireEvent.click(within(log).getByRole('button', { name: 'Yanıtlanan mesaj: Veli Yılmaz: message 100' }));

    expect(await screen.findByRole('button', { name: 'Son mesajlara dön' })).toBeVisible();
  });
});
