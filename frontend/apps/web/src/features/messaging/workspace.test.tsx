import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { delay, http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { Locale } from '@backroom/i18n';
import { AuthProvider } from '@/features/auth';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { createMessagingApi, MessagingApiContext } from './api';
import { messagesKey } from './queries';
import { forgetSeqs, lastSeenSeq } from './seq-memory';
import { Workspace } from './workspace';

const CELL = 'https://cell.example.test';
const ORG = '0b6f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a11';
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const OTHER_ID = '00000000-0000-4000-8000-000000000002';
const GENERAL = { id: 'c-general', name: 'general', createdAt: '2026-11-20T09:00:00Z' };
const RANDOM = { id: 'c-random', name: 'random', createdAt: '2026-11-20T09:05:00Z' };

type Posted = { url: string; body: { clientMessageId: string; text: string }; authorization: string | null };
let posted: Posted[] = [];
let requested: string[] = [];

const server = setupServer();

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
  // jsdom has no layout: the log is 400 px tall, a row 40 px, and scrollTop can be set.
  const tops = new WeakMap<Element, number>();
  const isLog = (element: Element) => element.getAttribute('role') === 'log';
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

afterEach(() => {
  server.resetHandlers();
  posted = [];
  requested = [];
  forgetSeqs();
});

afterAll(() => {
  server.close();
});

const message = (seq: number, text = `message ${String(seq)}`, authorAccountId = OTHER_ID) => ({
  id: `m-${String(seq)}`,
  seq,
  clientMessageId: `00000000-0000-4000-8000-${String(seq).padStart(12, '0')}`,
  authorAccountId,
  text,
  createdAt: '2026-11-22T10:00:00Z',
});

/** A cell with channels and, per channel, messages with seq 1..count (newest first, 50 a page). */
type CellOptions = {
  channels?: (typeof GENERAL)[];
  counts?: Record<string, number>;
  texts?: Record<number, string>;
  channelPages?: boolean;
};

function serveCell({
  channels = [GENERAL, RANDOM],
  counts = { [GENERAL.id]: 3 },
  texts = {},
  channelPages = false,
}: CellOptions = {}) {
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
    http.get(`${CELL}/api/v1/orgs/${ORG}/channels`, ({ request }) => {
      requested.push(request.url);
      const after = new URL(request.url).searchParams.get('after');
      if (channelPages) {
        return after
          ? HttpResponse.json({ channels: channels.slice(1), next: null })
          : HttpResponse.json({ channels: channels.slice(0, 1), next: channels[0]?.id });
      }

      return HttpResponse.json({ channels, next: null });
    }),
    http.get(`${CELL}/api/v1/orgs/${ORG}/channels/:channelId/messages`, ({ request, params }) => {
      requested.push(request.url);
      const count = counts[String(params.channelId)] ?? 0;
      const before = new URL(request.url).searchParams.get('before');
      const first = before === null ? count : Number(before) - 1;
      const seqs: number[] = [];
      for (let seq = first; seq >= 1 && seqs.length < 50; seq -= 1) seqs.push(seq);
      const last = seqs.at(-1) ?? 1;
      return HttpResponse.json({
        messages: seqs.map((seq) => message(seq, texts[seq])),
        next: last > 1 ? last : null,
      });
    }),
  );
}

function servePost(handler: (body: Posted['body']) => Response | Promise<Response>) {
  server.use(
    http.post(`${CELL}/api/v1/orgs/${ORG}/channels/:channelId/messages`, async ({ request }) => {
      const body = (await request.json()) as Posted['body'];
      posted.push({ url: request.url, body, authorization: request.headers.get('Authorization') });
      return handler(body);
    }),
  );
}

type Org = { id: string; name: string; cell: { id: string; url: string } };
const ORG_A: Org = { id: ORG, name: 'Acme', cell: { id: 'a', url: CELL } };

function renderWorkspace(locale: Locale = 'en', organization: Org = ORG_A) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = (current: Org) => (
    <LocaleProvider locale={locale}>
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={fakeAuthClient()}>
          <MessagingApiContext.Provider
            value={(cell) => createMessagingApi(cell.url, () => Promise.resolve('cell-token'))}
          >
            <Workspace organization={current} onSwitch={vi.fn()} />
          </MessagingApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>
  );
  const result = render(ui(organization));
  return {
    ...result,
    queryClient,
    /** The same tree for another organization, as when the person switches. */
    switchTo: (next: Org) => {
      result.rerender(ui(next));
    },
  };
}

/** The log once its messages are in: scrolls to the end, as the browser does after the list sets its position. */
async function openLog(name = 'Messages in #general') {
  const log = await screen.findByRole('log', { name });
  await waitFor(() => {
    expect(within(log).queryAllByRole('listitem').length).toBeGreaterThan(0);
  });
  fireEvent.scroll(log);
  return log;
}

const composer = (name = 'Message #general') => screen.getByRole<HTMLTextAreaElement>('textbox', { name });

describe('Channels', () => {
  it('ChannelList_AfterSignIn_ListsChannelsAndOpensTheFirst', async () => {
    serveCell();
    renderWorkspace();

    const nav = await screen.findByRole('navigation');
    expect(await within(nav).findByRole('button', { name: 'general' })).toHaveAttribute(
      'aria-current',
      'true',
    );
    expect(within(nav).getByRole('button', { name: 'random' })).not.toHaveAttribute('aria-current');
    expect(await screen.findByRole('log', { name: 'Messages in #general' })).toBeInTheDocument();
  });

  it('ChannelList_WithANextPage_LoadsTheRestOnRequest', async () => {
    serveCell({ channelPages: true });
    renderWorkspace();

    await screen.findByRole('button', { name: 'general' });
    expect(screen.queryByRole('button', { name: 'random' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Show more channels' }));

    expect(await screen.findByRole('button', { name: 'random' })).toBeInTheDocument();
    expect(requested.some((url) => url.includes(`after=${GENERAL.id}`))).toBe(true);
  });

  it('ChannelList_SelectingAChannel_ShowsItsMessages', async () => {
    serveCell({ counts: { [GENERAL.id]: 3, [RANDOM.id]: 2 }, texts: { 2: 'random two' } });
    renderWorkspace();

    fireEvent.click(await screen.findByRole('button', { name: 'random' }));

    expect(await screen.findByRole('log', { name: 'Messages in #random' })).toBeInTheDocument();
    expect(await screen.findByText('random two')).toBeInTheDocument();
  });

  it('CreateChannel_WithAName_PostsItAndOpensTheNewChannel', async () => {
    serveCell();
    const created = { id: 'c-new', name: 'design', createdAt: '2026-11-22T10:00:00Z' };
    let body: unknown;
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/channels`, async ({ request }) => {
        body = await request.json();
        serveCell({ channels: [GENERAL, RANDOM, created], counts: {} });
        return HttpResponse.json(created, { status: 201 });
      }),
    );
    renderWorkspace();

    fireEvent.click(await screen.findByRole('button', { name: 'Add channel' }));
    const dialog = screen.getByRole('dialog', { name: 'Create channel' });
    expect(within(dialog).getByLabelText('Channel name')).toHaveFocus();
    fireEvent.change(within(dialog).getByLabelText('Channel name'), { target: { value: '  design ' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('log', { name: 'Messages in #design' })).toBeInTheDocument();
    expect(body).toEqual({ name: 'design' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'design' })).toHaveAttribute('aria-current', 'true');
  });

  it('CreateChannel_NameTaken_ShowsTheTranslatedError', async () => {
    serveCell();
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/channels`, () =>
        HttpResponse.json({ code: 'messaging.channel_name_taken', traceId: 't' }, { status: 409 }),
      ),
    );
    renderWorkspace();

    fireEvent.click(await screen.findByRole('button', { name: 'Add channel' }));
    const input = screen.getByLabelText('Channel name');
    fireEvent.change(input, { target: { value: 'general' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText('A channel with this name already exists.')).toBeInTheDocument();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('CreateChannel_NotAllowed_ShowsThePermissionError', async () => {
    serveCell();
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/channels`, () =>
        HttpResponse.json({ code: 'permissions.forbidden', traceId: 't' }, { status: 403 }),
      ),
    );
    renderWorkspace();

    fireEvent.click(await screen.findByRole('button', { name: 'Add channel' }));
    fireEvent.change(screen.getByLabelText('Channel name'), { target: { value: 'ops' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      "You don't have permission to create channels.",
    );
  });

  it('CreateChannel_BlankOrTooLongName_IsRefusedWithoutARequest', async () => {
    serveCell();
    const create = vi.fn();
    server.use(
      http.post(`${CELL}/api/v1/orgs/${ORG}/channels`, () => {
        create();
        return HttpResponse.json({}, { status: 500 });
      }),
    );
    renderWorkspace();

    fireEvent.click(await screen.findByRole('button', { name: 'Add channel' }));
    fireEvent.change(screen.getByLabelText('Channel name'), { target: { value: '   ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(
      await screen.findByText('The name must be 1–80 characters without control characters.'),
    ).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Channel name'), { target: { value: 'a'.repeat(81) } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(
      await screen.findByText('The name must be 1–80 characters without control characters.'),
    ).toBeInTheDocument();
    expect(create).not.toHaveBeenCalled();
  });

  it('CreateChannel_Escape_ClosesTheDialogAndReturnsFocus', async () => {
    serveCell();
    renderWorkspace();

    const add = await screen.findByRole('button', { name: 'Add channel' });
    add.focus();
    fireEvent.click(add);
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(add).toHaveFocus();
  });
});

describe('Messages', () => {
  it('MessageList_OnOpen_ShowsTheNewestMessagesWithNamesAndPagesBackOnScroll', async () => {
    serveCell({ counts: { [GENERAL.id]: 120 } });
    renderWorkspace();

    const log = await openLog();
    expect(await within(log).findByText('message 120')).toBeInTheDocument();
    expect(within(log).queryByText('message 70')).not.toBeInTheDocument();
    expect(within(log).getAllByText('Veli Yılmaz').length).toBeGreaterThan(0);
    expect(requested.filter((url) => url.includes('/messages'))).toHaveLength(1);
    expect(requested.find((url) => url.includes('/messages'))).not.toContain('before=');
    expect(lastSeenSeq(ORG, GENERAL.id)).toBe(120);

    // Scrolling to the top asks for the page before the oldest one loaded (seq 71).
    log.scrollTop = 0;
    fireEvent.scroll(log);

    await waitFor(() => {
      expect(requested.some((url) => url.includes('before=71'))).toBe(true);
    });
    // The earlier page is added above and the position is kept: the person still reads seq 71, and seq 70 is next to it.
    await waitFor(() => {
      expect(log.scrollTop).toBeGreaterThan(0);
    });
    fireEvent.scroll(log);
    expect(await within(log).findByText('message 70')).toBeInTheDocument();
    expect(lastSeenSeq(ORG, GENERAL.id)).toBe(120);
  });

  it('MessageList_ShortChannel_DoesNotAskForEarlierMessages', async () => {
    serveCell({ counts: { [GENERAL.id]: 3 } });
    renderWorkspace();

    const log = await openLog();

    expect(await within(log).findByText('message 3')).toBeInTheDocument();
    expect(requested.filter((url) => url.includes('/messages'))).toHaveLength(1);
  });

  it('MessageList_TextWithMarkup_IsShownAsText', async () => {
    serveCell({ texts: { 3: '<b>x</b> <img src=x onerror=alert(1)>\nsecond line' } });
    renderWorkspace();

    const log = await openLog();

    const text = await within(log).findByText(/second line/);
    expect(text).toHaveTextContent('<b>x</b> <img src=x onerror=alert(1)>');
    expect(text.querySelector('b, img')).toBeNull();
    expect(log.querySelector('.bm-text b, .bm-text img')).toBeNull();
  });

  it('MessageList_EmptyChannel_SaysSo', async () => {
    serveCell({ counts: {} });
    renderWorkspace();

    expect(await screen.findByText('No messages yet. Write the first one.')).toBeInTheDocument();
  });
});

describe('Composer', () => {
  it('Send_WhenTheFieldIsEmpty_IsDisabled', async () => {
    serveCell();
    renderWorkspace();
    await openLog();

    const send = screen.getByRole('button', { name: 'Send' });
    expect(send).toBeDisabled();
    fireEvent.change(composer(), { target: { value: '   \n ' } });
    expect(send).toBeDisabled();
    fireEvent.change(composer(), { target: { value: 'hi' } });
    expect(send).toBeEnabled();
  });

  it('Send_EnterSendsAndShiftEnterDoesNot', async () => {
    serveCell();
    servePost((body) =>
      HttpResponse.json(
        { ...message(4, body.text, OWN_ID), clientMessageId: body.clientMessageId },
        { status: 201 },
      ),
    );
    renderWorkspace();
    await openLog();

    fireEvent.change(composer(), { target: { value: 'line one' } });
    const shiftEnter = fireEvent.keyDown(composer(), { key: 'Enter', shiftKey: true });
    expect(shiftEnter).toBe(true);
    expect(posted).toHaveLength(0);

    const enter = fireEvent.keyDown(composer(), { key: 'Enter' });
    expect(enter).toBe(false);
    await waitFor(() => {
      expect(posted).toHaveLength(1);
    });
    expect(composer()).toHaveValue('');
  });

  it('Send_OnSuccess_ShowsTheMessageAtOnceThenTheServersOne', async () => {
    serveCell();
    servePost(async (body) => {
      await delay(50);
      return HttpResponse.json(
        { ...message(4, body.text, OWN_ID), clientMessageId: body.clientMessageId },
        { status: 201 },
      );
    });
    renderWorkspace();
    const log = await openLog();

    fireEvent.change(composer(), { target: { value: 'hello\r\nworld  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    // Optimistic: there before the server answers.
    expect(await within(log).findByText(/hello/)).toBeInTheDocument();
    expect(within(log).getByText('Sending…')).toBeInTheDocument();
    await waitFor(() => {
      expect(within(log).queryByText('Sending…')).not.toBeInTheDocument();
    });
    expect(within(log).getAllByText(/hello/)).toHaveLength(1);
    expect(within(log).getAllByText('You').length).toBeGreaterThan(0);
    expect(posted).toHaveLength(1);
    expect(posted[0]?.body.text).toBe('hello\nworld');
    expect(posted[0]?.body.clientMessageId).toMatch(/^[0-9a-f-]{36}$/);
    expect(posted[0]?.authorization).toBe('Bearer cell-token');
    expect(lastSeenSeq(ORG, GENERAL.id)).toBe(4);
  });

  it('Send_OnFailure_OffersRetryWithTheSameClientMessageId', async () => {
    serveCell();
    let attempts = 0;
    servePost((body) => {
      attempts += 1;
      return attempts === 1
        ? HttpResponse.json({ code: 'server.unexpected', traceId: 't' }, { status: 500 })
        : HttpResponse.json(
            { ...message(4, body.text, OWN_ID), clientMessageId: body.clientMessageId },
            { status: 200 },
          );
    });
    renderWorkspace();
    const log = await openLog();

    fireEvent.change(composer(), { target: { value: 'will fail once' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await within(log).findByText("Couldn't send.", { exact: false })).toBeInTheDocument();
    fireEvent.click(within(log).getByRole('button', { name: 'Try again' }));

    await waitFor(() => {
      expect(within(log).queryByText("Couldn't send.", { exact: false })).not.toBeInTheDocument();
    });
    expect(posted).toHaveLength(2);
    expect(posted[1]?.body.clientMessageId).toBe(posted[0]?.body.clientMessageId);
    expect(within(log).getAllByText('will fail once')).toHaveLength(1);
  });

  it('Send_FailedMessage_CanBeDiscarded', async () => {
    serveCell();
    servePost(() => HttpResponse.json({ code: 'server.unexpected', traceId: 't' }, { status: 500 }));
    renderWorkspace();
    const log = await openLog();

    fireEvent.change(composer(), { target: { value: 'drop me' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await within(log).findByRole('button', { name: 'Discard' });
    fireEvent.click(within(log).getByRole('button', { name: 'Discard' }));

    expect(within(log).queryByText('drop me')).not.toBeInTheDocument();
  });

  it('Counter_NearTheLimit_ShowsHowManyCharactersAreLeft', async () => {
    serveCell();
    renderWorkspace();
    await openLog();

    expect(screen.queryByText(/characters left/)).not.toBeInTheDocument();
    fireEvent.change(composer(), { target: { value: 'a'.repeat(3900) } });

    expect(screen.getByText('100 characters left')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
  });

  it('Counter_OverTheLimit_BlocksSendingAndSaysByHowMuch', async () => {
    serveCell();
    servePost(() => HttpResponse.json({}, { status: 500 }));
    renderWorkspace();
    await openLog();

    fireEvent.change(composer(), { target: { value: 'a'.repeat(4005) } });

    expect(screen.getByText(/characters over the limit/)).toHaveTextContent(
      '5 characters over the limit. Shorten the message to send it.',
    );
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(composer()).toHaveAttribute('aria-invalid', 'true');
    fireEvent.keyDown(composer(), { key: 'Enter' });
    expect(posted).toHaveLength(0);
    expect(composer()).toHaveValue('a'.repeat(4005));

    // Exactly the limit is allowed.
    fireEvent.change(composer(), { target: { value: 'a'.repeat(4000) } });
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
  });

  it('Counter_CountsTheNormalizedText_LikeTheServer', async () => {
    serveCell();
    renderWorkspace();
    await openLog();

    // Line endings count once and a combining accent is one character after NFC; the ends are trimmed.
    fireEvent.change(composer(), { target: { value: `  ${'a\r\n'.repeat(1900)}${'é'.repeat(2000)}  ` } });

    // 1,900 x (letter + one line feed) + 2,000 characters = 5,800, not 7,700 (CRLF as two, accents as two).
    expect(screen.getByText(/characters over the limit/)).toHaveTextContent('1800 characters over the limit');
  });
});

describe('Races and isolation', () => {
  const messagesUrl = `${CELL}/api/v1/orgs/${ORG}/channels/:channelId/messages`;

  it('Send_WhileARefetchIsInFlight_KeepsTheConfirmedMessage', async () => {
    serveCell();
    servePost(async (body) => {
      await delay(30);
      return HttpResponse.json(
        { ...message(4, body.text, OWN_ID), clientMessageId: body.clientMessageId },
        { status: 201 },
      );
    });
    const { queryClient } = renderWorkspace();
    const log = await openLog();

    // A refetch that started before the answer and returns the page as it was.
    server.use(
      http.get(messagesUrl, async () => {
        await delay(300);
        return HttpResponse.json({ messages: [message(3), message(2), message(1)], next: null });
      }),
    );
    void queryClient.invalidateQueries({ queryKey: messagesKey(ORG, GENERAL.id) });
    fireEvent.change(composer(), { target: { value: 'sent during a refetch' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => {
      expect(within(log).queryByText('Sending…')).not.toBeInTheDocument();
    });
    await new Promise((resolve) => setTimeout(resolve, 450));
    await waitFor(() => {
      expect(queryClient.isFetching()).toBe(0);
    });

    expect(within(log).getAllByText('sent during a refetch')).toHaveLength(1);
  });

  it('Send_BeforeTheFirstPageHasLoaded_LoadsTheChannelAndShowsTheMessage', async () => {
    serveCell();
    server.use(
      http.get(messagesUrl, async () => {
        await delay(200);
        return HttpResponse.json({ messages: [message(3), message(2), message(1)], next: null });
      }),
    );
    servePost((body) =>
      HttpResponse.json(
        { ...message(4, body.text, OWN_ID), clientMessageId: body.clientMessageId },
        { status: 201 },
      ),
    );
    renderWorkspace();
    await screen.findByRole('textbox', { name: 'Message #general' });

    fireEvent.change(composer(), { target: { value: 'sent too early' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    const log = await screen.findByRole('log', { name: 'Messages in #general' });
    await waitFor(() => {
      expect(within(log).getByText('sent too early')).toBeInTheDocument();
    });
    expect(screen.queryByText('Loading…')).not.toBeInTheDocument();
  });

  it('ChannelSwitch_KeepsEachChannelsUnsentMessagesAndDraftApart', async () => {
    serveCell({ counts: { [GENERAL.id]: 2, [RANDOM.id]: 2 } });
    servePost(() => HttpResponse.json({ code: 'server.unexpected', traceId: 't' }, { status: 500 }));
    renderWorkspace();
    const general = await openLog();

    fireEvent.change(composer(), { target: { value: 'fails in general' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await within(general).findByRole('button', { name: 'Discard' });
    fireEvent.change(composer(), { target: { value: 'half-written draft' } });

    fireEvent.click(screen.getByRole('button', { name: 'random' }));
    const random = await openLog('Messages in #random');

    expect(within(random).queryByText('fails in general')).not.toBeInTheDocument();
    expect(within(random).queryByRole('button', { name: 'Discard' })).not.toBeInTheDocument();
    expect(composer('Message #random')).toHaveValue('');

    fireEvent.click(screen.getByRole('button', { name: 'general' }));
    const again = await openLog();
    expect(within(again).getByText('fails in general')).toBeInTheDocument();
  });

  it('OrganizationSwitch_IgnoresRequestsOfTheOrganizationLeft', async () => {
    const ORG_B = '9d3f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a22';
    server.use(
      http.get(`${CELL}/api/v1/orgs/${ORG}/channels`, () =>
        HttpResponse.json({ channels: [GENERAL], next: null }),
      ),
      http.get(`${CELL}/api/v1/orgs/${ORG}/members`, () => HttpResponse.json({ members: [], next: null })),
      http.get(messagesUrl, async () => {
        await delay(250);
        return HttpResponse.json({ messages: [message(1, 'organization A message')], next: null });
      }),
      http.get(`${CELL}/api/v1/orgs/${ORG_B}/channels`, () =>
        HttpResponse.json({ channels: [{ ...GENERAL, id: 'b-general', name: 'bgeneral' }], next: null }),
      ),
      http.get(`${CELL}/api/v1/orgs/${ORG_B}/members`, () => HttpResponse.json({ members: [], next: null })),
      http.get(`${CELL}/api/v1/orgs/${ORG_B}/channels/b-general/messages`, () =>
        HttpResponse.json({ messages: [message(1, 'organization B message')], next: null }),
      ),
    );
    const { switchTo } = renderWorkspace();
    await screen.findByRole('log', { name: 'Messages in #general' });

    switchTo({ id: ORG_B, name: 'Globex', cell: { id: 'a', url: CELL } });
    const log = await screen.findByRole('log', { name: 'Messages in #bgeneral' });
    await within(log).findByText('organization B message');
    await new Promise((resolve) => setTimeout(resolve, 350));

    expect(screen.queryByText('organization A message')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Globex' })).toBeInTheDocument();
  });

  it('Send_WithTheButton_ReturnsFocusToTheField', async () => {
    serveCell();
    servePost((body) =>
      HttpResponse.json(
        { ...message(4, body.text, OWN_ID), clientMessageId: body.clientMessageId },
        { status: 201 },
      ),
    );
    renderWorkspace();
    await openLog();

    fireEvent.change(composer(), { target: { value: 'focus me back' } });
    screen.getByRole('button', { name: 'Send' }).focus();
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(composer()).toHaveFocus();
  });

  it('MessageLog_IsNotLive_AndTheComposerShowsItsKeyboardHint', async () => {
    serveCell();
    renderWorkspace();
    const log = await openLog();

    expect(log).toHaveAttribute('aria-live', 'off');
    expect(screen.getByText('Enter to send · Shift+Enter for a new line')).toBeInTheDocument();
    expect(composer()).toHaveAccessibleDescription(/Enter to send/);
  });
});

describe('Accessibility and language', () => {
  it('MainView_HasNoAxeViolations', async () => {
    serveCell({ counts: { [GENERAL.id]: 5 } });
    const { container } = renderWorkspace();
    await openLog();
    await within(container).findByText('message 5');

    const results = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } });

    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });

  it('CreateDialog_HasNoAxeViolations', async () => {
    serveCell();
    renderWorkspace();
    fireEvent.click(await screen.findByRole('button', { name: 'Add channel' }));

    // The page landmark (main) comes from the signed-in page around the workspace, which this test does not render.
    const results = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false }, region: { enabled: false } },
    });

    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });

  it('MainView_InTurkish_UsesTheDesignTexts', async () => {
    serveCell();
    renderWorkspace('tr');

    expect(await screen.findByRole('heading', { name: 'Kanallar' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Kanal ekle' }));
    expect(screen.getByRole('dialog', { name: 'Kanal oluştur' })).toBeInTheDocument();
    expect(await screen.findByText('Enter gönderir · Shift+Enter yeni satır')).toBeInTheDocument();
    expect(screen.getByLabelText('Kanal adı')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'İptal' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Oluştur' })).toBeInTheDocument();
  });
});
