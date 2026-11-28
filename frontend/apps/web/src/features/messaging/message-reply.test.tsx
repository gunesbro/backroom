import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import type { LexicalEditor } from 'lexical';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionStatus, RealtimeClient, RealtimeEventName, RealtimeEvents } from '@backroom/realtime';
import { AuthProvider } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { MessagingApiContext, type Message, type MessageQuote, type MessagingApi } from './api';
import { RealtimeContext } from './realtime-context';
import type { Block } from './rich-text/blocks';
import { $writeBlocks } from './rich-text/lexical-blocks';
import { forgetSeqs } from './seq-memory';
import { Workspace } from './workspace';

const flags = vi.hoisted(() => ({ reply: true }));

vi.mock('@/config', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/config')>();
  return {
    config: {
      ...original.config,
      richTextEnabled: true,
      messageEditEnabled: true,
      get messageReplyEnabled() {
        return flags.reply;
      },
    },
  };
});

const ORG = 'org-1';
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const OTHER_ID = '00000000-0000-4000-8000-000000000002';
const GENERAL = { id: 'c-general', name: 'general', createdAt: '2026-11-20T09:00:00Z' };
const RANDOM = { id: 'c-random', name: 'random', createdAt: '2026-11-20T09:05:00Z' };
const GONE_TEXT = 'The message you replied to was deleted or no longer exists. Your message was not sent.';

type WithEditor = HTMLElement & { __lexicalEditor?: LexicalEditor };
type SendInput = Parameters<MessagingApi['sendMessage']>[2];

const paragraph = (text: string): Block[] => [{ type: 'paragraph', children: [{ type: 'text', text }] }];

/** Odd seqs are Veli's, even ones the signed-in person's. */
const message = (seq: number, extra: Partial<Message> = {}): Message => {
  const own = seq % 2 === 0;
  const text = `message ${String(seq)}`;
  return {
    id: `m-${String(seq)}`,
    seq,
    versionSeq: seq,
    clientMessageId: `client-${String(seq)}`,
    authorAccountId: own ? OWN_ID : OTHER_ID,
    text,
    blocks: paragraph(text),
    createdAt: '2026-11-22T10:00:00Z',
    ...extra,
  };
};

const quoteOf = (seq: number, text = `message ${String(seq)}`): MessageQuote => ({
  messageId: `m-${String(seq)}`,
  deleted: false,
  authorAccountId: seq % 2 === 0 ? OWN_ID : OTHER_ID,
  text,
});

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

/** The general channel holds `messages` (oldest first); random holds two plain ones. */
function fakeApi(
  extra: Partial<MessagingApi> = {},
  messages: Message[] = [1, 2, 3, 4].map((n) => message(n)),
) {
  const api: MessagingApi = {
    listConversations: () => Promise.resolve({ conversations: [GENERAL, RANDOM], next: null }),
    browseChannels: () => Promise.resolve({ channels: [GENERAL, RANDOM], next: null }),
    joinConversation: vi.fn(),
    leaveConversation: vi.fn(),
    createChannel: vi.fn(),
    getConversation: (_org: string, conversationId: string) =>
      Promise.resolve({
        id: conversationId,
        kind: 'public_channel' as const,
        name: conversationId === RANDOM.id ? RANDOM.name : GENERAL.name,
        description: null,
        createdAt: GENERAL.createdAt,
        archivedAt: null,
        memberCount: 2,
        ownerAccountId: null,
        role: 'member' as const,
      }),
    updateConversation: vi.fn(),
    archiveConversation: vi.fn(),
    unarchiveConversation: vi.fn(),
    listConversationMembers: () => Promise.resolve({ members: [], next: null }),
    addConversationMembers: vi.fn(),
    transferOwnership: vi.fn(),
    listMyPermissions: () => Promise.resolve([]),
    listMessages: vi.fn((_org: string, conversationId: string) =>
      Promise.resolve({
        messages: conversationId === RANDOM.id ? [message(2), message(1)] : messages.toReversed(),
        next: null,
      }),
    ),
    sendMessage: vi.fn(),
    editMessage: vi.fn(),
    deleteMessage: vi.fn(() => Promise.resolve()),
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
  return api;
}

beforeAll(async () => {
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
  // The editor is a lazy chunk; loading it once keeps the cold load out of the first test's wait.
  await import('./rich-text/rich-field');
});

beforeEach(() => {
  flags.reply = true;
});

function renderWorkspace(api: MessagingApi, realtime: RealtimeClient = fakeRealtime().client) {
  forgetSeqs();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <LocaleProvider locale="en">
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={fakeAuthClient()}>
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
  return { ...view, queryClient };
}

async function openLog(channel = 'general') {
  const log = await screen.findByRole('feed', { name: `Messages in #${channel}` });
  await waitFor(() => {
    expect(within(log).queryAllByRole('article').length).toBeGreaterThan(0);
  });
  await screen.findByRole('textbox', { name: `Message #${channel}` });
  fireEvent.scroll(log);
  return log;
}

/** The row whose message says `text` (a quote of it in another row does not count). */
const rowOf = (log: HTMLElement, text: string) =>
  within(log)
    .getAllByText(text)
    .find((element) => !element.closest('.bm-reply-quote'))
    ?.closest<HTMLElement>('[role="article"]') as HTMLElement;

/** Puts the pointer over a row and returns queries within it (its action bar is then built). */
function hover(row: HTMLElement) {
  fireEvent.pointerEnter(row, { pointerType: 'mouse' });
  return within(row);
}

async function replyTo(log: HTMLElement, text: string) {
  fireEvent.click(await hover(rowOf(log, text)).findByRole('button', { name: 'Reply' }));
}

const composer = (channel = 'general') =>
  screen.getByRole<WithEditor>('textbox', { name: `Message #${channel}` });

function typeInComposer(text: string, channel = 'general') {
  const editor = composer(channel).__lexicalEditor as LexicalEditor;
  act(() => {
    editor.update(
      () => {
        $writeBlocks(paragraph(text));
      },
      { discrete: true },
    );
  });
}

/** The bar above the composer's field, if there is one. */
const replyBar = () => document.querySelector<HTMLElement>('.bm-replybar');

const live = {
  edited: (seq: number, versionSeq: number, text: string): RealtimeEvents['MessageEdited'] => ({
    orgId: ORG,
    conversationId: GENERAL.id,
    messageId: `m-${String(seq)}`,
    versionSeq,
    changeSeq: versionSeq,
    blocks: paragraph(text),
    text,
    editedAt: '2026-11-22T10:30:00Z',
    epoch: 'e1',
  }),
  deleted: (seq: number, versionSeq: number): RealtimeEvents['MessageDeleted'] => ({
    orgId: ORG,
    conversationId: GENERAL.id,
    messageId: `m-${String(seq)}`,
    versionSeq,
    changeSeq: versionSeq,
    epoch: 'e1',
  }),
  posted: (
    replyTo: RealtimeEvents['MessagePosted']['message']['replyTo'],
  ): RealtimeEvents['MessagePosted'] => ({
    orgId: ORG,
    channelId: GENERAL.id,
    message: {
      id: 'm-9',
      seq: 9,
      clientMessageId: 'client-9',
      authorAccountId: OTHER_ID,
      text: 'live reply',
      blocks: paragraph('live reply'),
      createdAt: '2026-11-22T10:05:00Z',
      replyTo,
    },
  }),
};

/** Lets what an event or a request started finish, so a negative check cannot pass just because it ran early. */
const settle = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });

const NO_CONTRAST = { 'color-contrast': { enabled: false }, region: { enabled: false } };

/** Four plain messages, then message 5, which replies to `quote` (message 1 by default). */
const withReply = (quote: MessageQuote = quoteOf(1)) => [
  ...[1, 2, 3, 4].map((n) => message(n)),
  message(5, { replyTo: quote }),
];

const sendEcho = vi.fn((_org: string, _channel: string, input: SendInput) =>
  Promise.resolve(
    message(6, {
      clientMessageId: input.clientMessageId,
      text: input.text,
      blocks: paragraph(input.text),
      ...(input.replyToMessageId ? { replyTo: quoteOf(3) } : {}),
    }),
  ),
);

describe('Reply target', () => {
  it('ReplyButton_OnSomeoneElsesMessage_ShowsTheBarAndFocusesTheComposer', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();

    await replyTo(log, 'message 3');

    expect(replyBar()).toHaveTextContent('Replying to Veli Yilmaz');
    expect(screen.getByText('· message 3')).toBeInTheDocument();
    await waitFor(() => {
      expect(composer()).toHaveFocus();
    });
  });

  it('ReplyButton_OnOwnMessage_Works', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();

    await replyTo(log, 'message 2');

    expect(replyBar()).toHaveTextContent('Replying to Alice Example');
  });

  it('ReplyButton_OnAPendingMessage_IsNotThere', async () => {
    renderWorkspace(fakeApi({ sendMessage: vi.fn(() => new Promise<Message>(() => undefined)) }));
    const log = await openLog();
    typeInComposer('on its way');
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    const row = rowOf(log, 'on its way');
    fireEvent.pointerEnter(row, { pointerType: 'mouse' });

    expect(within(row).queryByRole('button', { name: 'Reply' })).toBeNull();
  });

  it('CloseButton_ClearsTheTargetAndFocusesTheComposer', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    await replyTo(log, 'message 3');

    fireEvent.click(screen.getByRole('button', { name: 'Cancel reply' }));

    expect(replyBar()).toBeNull();
    await waitFor(() => {
      expect(composer()).toHaveFocus();
    });
  });

  it('Escape_InTheComposer_ClearsTheTarget', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    await replyTo(log, 'message 3');

    fireEvent.keyDown(composer(), { key: 'Escape' });

    await waitFor(() => {
      expect(replyBar()).toBeNull();
    });
  });

  it('Escape_WithoutATarget_DoesNothing', async () => {
    renderWorkspace(fakeApi());
    await openLog();

    fireEvent.keyDown(composer(), { key: 'Escape' });

    expect(replyBar()).toBeNull();
  });

  it('Target_IsKeptPerConversationWhileSwitchingChannels', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    await replyTo(log, 'message 3');

    fireEvent.click(screen.getByRole('button', { name: 'random' }));
    await openLog('random');
    expect(replyBar()).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'general' }));
    await openLog();
    expect(replyBar()).toHaveTextContent('Replying to Veli Yilmaz');
  });

  it('Target_WhenTheOriginalIsDeletedLive_IsDroppedQuietly', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();
    await replyTo(log, 'message 3');

    realtime.emit('MessageDeleted', live.deleted(3, 30));

    await waitFor(() => {
      expect(replyBar()).toBeNull();
    });
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ReplyButton_WhenTheFlagIsOff_IsPassiveAndThereIsNoBar', async () => {
    flags.reply = false;
    renderWorkspace(fakeApi());
    const log = await openLog();

    const button = await hover(rowOf(log, 'message 3')).findByRole('button', { name: 'Reply' });
    fireEvent.click(button);

    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(replyBar()).toBeNull();
  });
});

describe('Sending a reply', () => {
  it('Send_WithATarget_CarriesTheReplyToMessageIdAndClearsTheBar', async () => {
    const sendMessage = vi.fn(sendEcho);
    renderWorkspace(fakeApi({ sendMessage }));
    const log = await openLog();
    await replyTo(log, 'message 3');
    typeInComposer('my answer');

    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => {
      expect(sendMessage).toHaveBeenCalledTimes(1);
    });
    expect(sendMessage.mock.calls[0]?.[2]).toMatchObject({ text: 'my answer', replyToMessageId: 'm-3' });
    expect(replyBar()).toBeNull();
    expect(
      await within(log).findByRole('button', { name: 'Replied message: Veli Yilmaz: message 3' }),
    ).toBeInTheDocument();
  });

  it('Send_WithoutATarget_HasNoReplyToMessageId', async () => {
    const sendMessage = vi.fn(sendEcho);
    renderWorkspace(fakeApi({ sendMessage }));
    await openLog();
    typeInComposer('plain');

    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() => {
      expect(sendMessage).toHaveBeenCalledTimes(1);
    });
    expect(sendMessage.mock.calls[0]?.[2]).not.toHaveProperty('replyToMessageId');
  });

  it('Send_WhileWaiting_DrawsTheQuoteFromTheCachedOriginal', async () => {
    renderWorkspace(fakeApi({ sendMessage: vi.fn(() => new Promise<Message>(() => undefined)) }));
    const log = await openLog();
    await replyTo(log, 'message 3');
    typeInComposer('waiting');

    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    const row = rowOf(log, 'waiting');
    expect(within(row).getByText('Veli Yilmaz')).toBeInTheDocument();
    expect(within(row).getByText('message 3')).toBeInTheDocument();
  });

  it.each([
    [400, 'messaging.reply_invalid', 'reply.deleted', GONE_TEXT],
    [400, 'messaging.reply_invalid', 'reply.not_found', GONE_TEXT],
    [
      403,
      'messaging.message_reply_unavailable',
      undefined,
      'Replying to a message is not available yet. Your message was not sent.',
    ],
    [400, 'messaging.reply_invalid', 'reply.id', "This message can't be sent. Check the text."],
  ])(
    'Send_Refused_%i_%s_%s_KeepsTheTextClearsTheTargetAndOffersNoRetry',
    async (status, code, reason, text) => {
      const sendMessage = vi.fn(() => Promise.reject(new ApiError(status, code, undefined, reason)));
      renderWorkspace(fakeApi({ sendMessage }));
      const log = await openLog();
      await replyTo(log, 'message 3');
      typeInComposer('my answer');

      fireEvent.click(screen.getByRole('button', { name: 'Send' }));

      await within(log).findByText(text);
      const row = rowOf(log, 'my answer');
      expect(within(row).queryByRole('button', { name: 'Retry' })).toBeNull();
      expect(within(row).getByRole('button', { name: 'Discard' })).toBeInTheDocument();
      expect(replyBar()).toBeNull();
    },
  );
});

describe('Quotes', () => {
  it('Quote_OnAReply_ShowsTheNameAndTheText', async () => {
    renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();

    const quote = within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' });

    expect(quote).toHaveTextContent('Veli Yilmaz');
    expect(quote).toHaveTextContent('message 1');
  });

  it('Quote_OfAnOwnMessage_UsesTheOwnName', async () => {
    renderWorkspace(fakeApi({}, withReply(quoteOf(2))));
    const log = await openLog();

    expect(
      within(log).getByRole('button', { name: 'Replied message: Alice Example: message 2' }),
    ).toBeInTheDocument();
  });

  it('Quote_OfADeletedOriginal_ShowsTheContractTextWithoutAName', async () => {
    renderWorkspace(fakeApi({}, withReply({ messageId: 'm-1', deleted: true })));
    const log = await openLog();

    const row = rowOf(log, 'message 5');

    expect(within(row).getByText('This message was deleted')).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: /Replied message/ })).toBeNull();
    expect(row.querySelector('.bm-reply-quote .ph-avatar')).toBeNull();
    expect(row.querySelector('.bm-reply-quote b')).toBeNull();
  });

  it('Quote_DeletedButCarryingAuthorAndText_ShowsNeither', async () => {
    renderWorkspace(
      fakeApi({}, withReply({ messageId: 'm-1', deleted: true, authorAccountId: OTHER_ID, text: 'secret' })),
    );
    const log = await openLog();

    expect(within(log).queryByText('secret')).toBeNull();
    expect(rowOf(log, 'message 5').querySelector('.bm-reply-quote b')).toBeNull();
  });

  it('Quote_WhenTheOriginalIsNotLoaded_IsAButton', async () => {
    renderWorkspace(fakeApi({}, [message(5, { replyTo: quoteOf(1) })]));
    const log = await openLog();

    expect(
      within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' }),
    ).toBeVisible();
  });

  it('Quote_WhenTheOriginalIsNotLoadedAndTheFlagIsOff_IsTextNotAButton', async () => {
    flags.reply = false;
    renderWorkspace(fakeApi({}, [message(5, { replyTo: quoteOf(1) })]));
    const log = await openLog();

    expect(within(log).queryByRole('button', { name: /Replied message/ })).toBeNull();
    expect(within(log).getByText('message 1')).toBeInTheDocument();
  });

  it('Quote_Activated_MarksTheOriginalForAMoment', async () => {
    renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();

    fireEvent.click(within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' }));

    await waitFor(() => {
      expect(rowOf(log, 'message 1')).toHaveClass('bm-message--flash');
    });
    expect(rowOf(log, 'message 3')).not.toHaveClass('bm-message--flash');
  });

  it('Quote_ActivatedWithTheKeyboard_MovesFocusToTheOriginal', async () => {
    renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();
    const quote = within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' });

    // A click made with Enter or Space has no pointer position (detail 0).
    fireEvent.click(quote, { detail: 0 });

    await waitFor(() => {
      expect(rowOf(log, 'message 1')).toHaveFocus();
    });
  });

  it('LiveEdit_OfTheOriginal_UpdatesItsQuotes', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi({}, withReply()), realtime.client);
    const log = await openLog();

    realtime.emit('MessageEdited', live.edited(1, 100, 'fresh text'));

    expect(
      await within(log).findByRole('button', { name: 'Replied message: Veli Yilmaz: fresh text' }),
    ).toBeInTheDocument();
  });

  it('LiveEdit_WithALongText_CutsTheQuoteLikeTheServer', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi({}, withReply()), realtime.client);
    const log = await openLog();

    realtime.emit('MessageEdited', live.edited(1, 100, `${'a'.repeat(199)}\u{1F600}tail`));

    await within(log).findByRole('button', { name: `Replied message: Veli Yilmaz: ${'a'.repeat(199)}` });
  });

  it('LiveEdit_NotNewerThanTheCache_LeavesTheQuoteAlone', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi({}, withReply()), realtime.client);
    const log = await openLog();

    realtime.emit('MessageEdited', live.edited(1, 1, 'stale'));
    await settle();

    expect(
      within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' }),
    ).toBeInTheDocument();
  });

  it('LiveDelete_OfTheOriginal_TurnsItsQuotesIntoDeletedOnes', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi({}, withReply()), realtime.client);
    const log = await openLog();

    realtime.emit('MessageDeleted', live.deleted(1, 100));

    await waitFor(() => {
      expect(within(rowOf(log, 'message 5')).getByText('This message was deleted')).toBeInTheDocument();
    });
    expect(within(log).queryByRole('button', { name: /Replied message/ })).toBeNull();
  });

  it('LiveEdit_AfterTheDelete_DoesNotBringTheQuoteBack', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi({}, withReply()), realtime.client);
    const log = await openLog();

    realtime.emit('MessageDeleted', live.deleted(1, 100));
    realtime.emit('MessageEdited', live.edited(1, 101, 'zombie'));
    await settle();

    expect(within(log).queryByText('zombie')).toBeNull();
  });

  it('LivePost_WithAQuote_IsDrawnWithIt', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();

    realtime.emit(
      'MessagePosted',
      live.posted({ messageId: 'm-2', authorAccountId: OWN_ID, text: 'message 2', deleted: false }),
    );

    expect(
      await within(log).findByRole('button', { name: 'Replied message: Alice Example: message 2' }),
    ).toBeInTheDocument();
  });

  it('LivePost_WithADeletedQuoteOfNulls_IsDrawnWithoutAuthor', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();

    realtime.emit(
      'MessagePosted',
      live.posted({ messageId: 'm-2', authorAccountId: null, text: null, deleted: true }),
    );

    await within(log).findByText('live reply');
    const row = rowOf(log, 'live reply');
    expect(within(row).getByText('This message was deleted')).toBeInTheDocument();
    expect(row.querySelector('.bm-reply-quote b')).toBeNull();
  });

  it('Quote_WhenTheFlagIsOff_IsStillDrawn', async () => {
    flags.reply = false;
    renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();

    expect(
      within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' }),
    ).toBeInTheDocument();
  });
});

describe('Deleted originals', () => {
  it('LiveDelete_WhileTheReplyIsBeingSent_RemovesTheOriginalFromItsQuote', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(
      fakeApi({ sendMessage: vi.fn(() => new Promise<Message>(() => undefined)) }),
      realtime.client,
    );
    const log = await openLog();
    await replyTo(log, 'message 3');
    typeInComposer('waiting');
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(within(rowOf(log, 'waiting')).getByText('message 3')).toBeInTheDocument();

    realtime.emit('MessageDeleted', live.deleted(3, 100));

    const row = rowOf(log, 'waiting');
    await waitFor(() => {
      expect(within(row).getByText('This message was deleted')).toBeInTheDocument();
    });
    expect(row.querySelector('.bm-reply-quote b')).toBeNull();
    expect(within(row).queryByText('message 3')).toBeNull();
  });

  it('Send_RefusedBecauseTheOriginalIsGone_KeepsNoNameOrTextOfIt', async () => {
    const sendMessage = vi.fn(() =>
      Promise.reject(new ApiError(400, 'messaging.reply_invalid', undefined, 'reply.deleted')),
    );
    renderWorkspace(fakeApi({ sendMessage }));
    const log = await openLog();
    await replyTo(log, 'message 3');
    typeInComposer('my answer');

    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await within(log).findByText(GONE_TEXT);
    const row = rowOf(log, 'my answer');
    expect(within(row).getByText('This message was deleted')).toBeInTheDocument();
    expect(row.querySelector('.bm-reply-quote b')).toBeNull();
    expect(within(row).queryByText('message 3')).toBeNull();
  });

  it('Refetch_BringingTheOriginalAsATombstone_RemovesItFromAFailedRepliesQuote', async () => {
    let current = [1, 2, 3, 4].map((n) => message(n));
    const listMessages = vi.fn(() => Promise.resolve({ messages: current.toReversed(), next: null }));
    const sendMessage = vi.fn(() => Promise.reject(new ApiError(500, 'server.error')));
    const { queryClient } = renderWorkspace(fakeApi({ listMessages, sendMessage }));
    const log = await openLog();
    await replyTo(log, 'message 3');
    typeInComposer('my answer');
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await within(log).findByRole('button', { name: 'Discard' });
    expect(within(rowOf(log, 'my answer')).getByText('message 3')).toBeInTheDocument();

    current = current.map((item) =>
      item.id === 'm-3'
        ? {
            id: 'm-3',
            seq: 3,
            versionSeq: 100,
            createdAt: item.createdAt,
            deletedAt: '2026-11-22T11:00:00Z',
            clientMessageId: '',
            authorAccountId: '',
            text: '',
          }
        : item,
    );
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ['orgs', ORG, 'conversations', GENERAL.id, 'messages'],
      });
    });

    await waitFor(() => {
      expect(within(rowOf(log, 'my answer')).getByText('This message was deleted')).toBeInTheDocument();
    });
    const row = rowOf(log, 'my answer');
    expect(row.querySelector('.bm-reply-quote b')).toBeNull();
    expect(within(row).queryByText('message 3')).toBeNull();
  });

  it('LiveDelete_OfTheOriginalOfAJustSentReply_RemovesItFromTheReplysQuote', async () => {
    const realtime = fakeRealtime();
    const sendMessage = vi.fn(sendEcho);
    renderWorkspace(fakeApi({ sendMessage }), realtime.client);
    const log = await openLog();
    await replyTo(log, 'message 3');
    typeInComposer('sent reply');
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await within(log).findByRole('button', { name: 'Replied message: Veli Yilmaz: message 3' });

    realtime.emit('MessageDeleted', live.deleted(3, 100));

    await waitFor(() => {
      expect(within(rowOf(log, 'sent reply')).getByText('This message was deleted')).toBeInTheDocument();
    });
  });

  it('Target_ShowsTheOriginalsNewTextAfterALiveEdit', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();
    await replyTo(log, 'message 3');

    realtime.emit('MessageEdited', live.edited(3, 100, 'edited text'));

    await waitFor(() => {
      expect(screen.getByText('· edited text')).toBeInTheDocument();
    });
  });

  it('Target_WhenTheOriginalIsNotInTheLoadedList_IsDroppedQuietly', async () => {
    const { queryClient } = renderWorkspace(fakeApi());
    const log = await openLog();
    await replyTo(log, 'message 3');

    // The page that held it is gone from the cache (the list keeps a few pages at most).
    act(() => {
      queryClient.setQueryData(['orgs', ORG, 'conversations', GENERAL.id, 'messages'], {
        pages: [{ messages: [message(4)], next: null }],
        pageParams: [undefined],
      });
    });

    await waitFor(() => {
      expect(replyBar()).toBeNull();
    });
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('DeleteRefused_PutsTheRepliesQuotesBack', async () => {
    const deleteMessage = vi.fn(() => Promise.reject(new ApiError(500, 'server.error')));
    renderWorkspace(fakeApi({ deleteMessage }, withReply(quoteOf(2))));
    const log = await openLog();
    const row = rowOf(log, 'message 2');
    fireEvent.pointerEnter(row, { pointerType: 'mouse' });
    fireEvent.click(await within(row).findByRole('button', { name: 'More' }));
    fireEvent.click(within(row).getByRole('menuitem', { name: 'Delete message' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(deleteMessage).toHaveBeenCalledTimes(1);
    });
    expect(
      await within(log).findByRole('button', { name: 'Replied message: Alice Example: message 2' }),
    ).toBeInTheDocument();
  });
});

describe('Highlight', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('Flash_EndsAfterAWhile', async () => {
    renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();
    vi.useFakeTimers({ shouldAdvanceTime: true });

    fireEvent.click(within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' }));
    await waitFor(() => {
      expect(rowOf(log, 'message 1')).toHaveClass('bm-message--flash');
    });
    act(() => {
      vi.advanceTimersByTime(2000);
    });

    expect(rowOf(log, 'message 1')).not.toHaveClass('bm-message--flash');
  });

  it('Flash_TimerIsClearedWhenTheListGoesAway', async () => {
    const view = renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const clear = vi.spyOn(globalThis, 'clearTimeout');

    fireEvent.click(within(log).getByRole('button', { name: 'Replied message: Veli Yilmaz: message 1' }));
    await waitFor(() => {
      expect(rowOf(log, 'message 1')).toHaveClass('bm-message--flash');
    });
    clear.mockClear();
    view.unmount();

    expect(clear).toHaveBeenCalled();
  });
});

describe('Focus', () => {
  it('Edit_InPlace_PutsTheCaretAtTheEndOfTheContent', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    const row = rowOf(log, 'message 2');
    fireEvent.pointerEnter(row, { pointerType: 'mouse' });
    fireEvent.click(await within(row).findByRole('button', { name: 'More' }));
    fireEvent.click(within(row).getByRole('menuitem', { name: 'Edit message' }));

    const input = await screen.findByRole('textbox', { name: 'Edit message' });

    await waitFor(() => {
      expect(input).toHaveFocus();
    });
    await waitFor(() => {
      const selection = window.getSelection();
      expect(selection?.anchorNode && input.contains(selection.anchorNode)).toBe(true);
      expect(selection?.anchorOffset).toBe('message 2'.length);
    });
  });
});

describe('Accessibility', () => {
  it('ReplyBar_HasNoAccessibilityViolations', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    await replyTo(log, 'message 3');

    const form = replyBar()?.closest('form') as HTMLElement;
    expect(form).not.toBeNull();
    const results = await axe.run(form, { rules: NO_CONTRAST });

    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });

  it('ReplyBar_IsPartOfTheFieldsDescription', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    await replyTo(log, 'message 3');

    const describedBy = composer().getAttribute('aria-describedby') ?? '';
    const text = describedBy
      .split(' ')
      .map((id) => document.getElementById(id)?.textContent ?? '')
      .join(' ');

    expect(text).toContain('Replying to Veli Yilmaz');
  });

  it('Quote_HasNoAccessibilityViolations', async () => {
    renderWorkspace(fakeApi({}, withReply()));
    const log = await openLog();

    const results = await axe.run(log, { rules: NO_CONTRAST });

    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});
