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
import { RealtimeContext } from './realtime-context';
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
    http.get(`${CELL}/api/v1/orgs/${ORG}/conversations/:channelId/messages`, ({ request, params }) => {
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
        return HttpResponse.json({
          items: seqs.map(wire),
          before: anchor - older > 1 ? anchor - older : null,
          after: anchor + newer < cell.count ? anchor + newer : null,
          anchor: { id: around, seq: anchor },
          epoch: EPOCH,
        });
      }

      if (query.has('epoch') && cell.epochStatus) return problem(cell.epochStatus);
      const after = query.get('after');
      if (after !== null) {
        // Oldest first, `next` forward (null: nothing newer).
        const seqs: number[] = [];
        for (let seq = Number(after) + 1; seq <= cell.count && seqs.length < limit; seq += 1) seqs.push(seq);
        const last = seqs.at(-1);
        return HttpResponse.json({
          items: seqs.map(wire),
          next: last !== undefined && last < cell.count ? last : null,
          epoch: EPOCH,
        });
      }

      const before = query.get('before');
      const first = before === null ? cell.count : Number(before) - 1;
      const seqs: number[] = [];
      for (let seq = first; seq >= 1 && seqs.length < limit; seq -= 1) seqs.push(seq);
      const last = seqs.at(-1) ?? 1;
      return HttpResponse.json({ items: seqs.map(wire), next: last > 1 ? last : null, epoch: EPOCH });
    }),
  );
}

function fakeRealtime() {
  const listeners = new Map<RealtimeEventName, ((payload: never) => void)[]>();
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
    onStatus: () => () => undefined,
    status: (): ConnectionStatus => 'connected',
  };
  return {
    client: client as unknown as RealtimeClient,
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
        tops.set(this, options.top ?? 0);
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
  await within(log).findByText('message 160');
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
    const before = reads('before').at(-1);
    expect(before?.searchParams.get('before')).toBe('76');
    expect(before?.searchParams.get('epoch')).toBe(EPOCH);

    scrollLog(log, 'end');
    await within(log).findByText('message 126');
    const after = reads('after').at(-1);
    expect(after?.searchParams.get('after')).toBe('125');
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

    // The read after 125 reached the newest message: the button goes, and nothing else was read to get there.
    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
    expect(reads('after')).toHaveLength(1);

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

    expect(await findInLog(log, 'message 160')).toBeInTheDocument();
    await waitFor(() => {
      expect(latestButton()).toBeNull();
    });
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
