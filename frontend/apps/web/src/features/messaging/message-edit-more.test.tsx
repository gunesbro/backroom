import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { $getSelection, type LexicalEditor } from 'lexical';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionStatus, RealtimeClient, RealtimeEventName, RealtimeEvents } from '@backroom/realtime';
import { AuthProvider } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { MessagingApiContext, type Message, type MessagingApi } from './api';
import { MessageList, type MessageEditing, type Row } from './message-list';
import { RealtimeContext } from './realtime-context';
import type { Block } from './rich-text/blocks';
import { forgetSeqs } from './seq-memory';
import { Workspace } from './workspace';

const flags = vi.hoisted(() => ({ edit: true }));

vi.mock('@/config', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/config')>();
  return {
    config: {
      ...original.config,
      richTextEnabled: true,
      get messageEditEnabled() {
        return flags.edit;
      },
    },
  };
});

// Focus, rollback and race rules around deleting and editing (the basic flows are in message-edit.test.tsx).

const ORG = 'org-1';
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const OTHER_ID = '00000000-0000-4000-8000-000000000002';
const GENERAL = { id: 'c-general', name: 'general', createdAt: '2026-11-20T09:00:00Z' };

type WithEditor = HTMLElement & { __lexicalEditor?: LexicalEditor };

// jsdom does not move the document's focus when the editor takes it; the editor then has a selection (it has none
// before, and none if it was never asked to focus).
const hasSelection = (input: WithEditor) =>
  input.__lexicalEditor?.read(() => $getSelection() !== null) ?? false;

const paragraph = (text: string): Block[] => [{ type: 'paragraph', children: [{ type: 'text', text }] }];

const message = (seq: number): Message => {
  const text = `message ${String(seq)}`;
  return {
    id: `m-${String(seq)}`,
    seq,
    versionSeq: seq,
    clientMessageId: `client-${String(seq)}`,
    authorAccountId: seq % 2 === 0 ? OWN_ID : OTHER_ID,
    text,
    blocks: paragraph(text),
    createdAt: '2026-11-22T10:00:00Z',
  };
};

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

function fakeApi(extra: Partial<MessagingApi> = {}) {
  const newest = [4, 3, 2, 1].map(message);
  const api: MessagingApi = {
    listConversations: () => Promise.resolve({ conversations: [GENERAL], next: null }),
    browseChannels: () => Promise.resolve({ channels: [GENERAL], next: null }),
    joinConversation: vi.fn(),
    leaveConversation: vi.fn(),
    createChannel: vi.fn(),
    getConversation: (_org: string, conversationId: string) =>
      Promise.resolve({
        id: conversationId,
        kind: 'public_channel' as const,
        name: GENERAL.name,
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
    listMessages: vi.fn(() => Promise.resolve({ messages: newest, next: null })),
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
  await import('./message-editor');
});

beforeEach(() => {
  flags.edit = true;
});

function renderWorkspace(api: MessagingApi, realtime: RealtimeClient = fakeRealtime().client) {
  forgetSeqs();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
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
}

async function openLog() {
  const log = await screen.findByRole('feed', { name: 'Messages in #general' });
  await waitFor(() => {
    expect(within(log).queryAllByRole('article').length).toBeGreaterThan(0);
  });
  await screen.findByRole('textbox', { name: 'Message #general' });
  fireEvent.scroll(log);
  return log;
}

const rowOf = (log: HTMLElement, text: string) =>
  within(log).getByText(text).closest<HTMLElement>('[role="article"]') as HTMLElement;

function hover(row: HTMLElement) {
  fireEvent.pointerEnter(row, { pointerType: 'mouse' });
  return within(row);
}

async function openMenu(row: HTMLElement) {
  fireEvent.click(await hover(row).findByRole('button', { name: 'More' }));
}

/** Lets what an event or a request started finish, so a negative check cannot pass just because it ran early. */
const settle = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });

const deletedEvent = (seq: number, versionSeq: number): RealtimeEvents['MessageDeleted'] => ({
  orgId: ORG,
  conversationId: GENERAL.id,
  messageId: `m-${String(seq)}`,
  versionSeq,
  changeSeq: versionSeq,
  epoch: 'e1',
});

async function askToDelete(log: HTMLElement, text: string) {
  const row = rowOf(log, text);
  await openMenu(row);
  fireEvent.click(within(row).getByRole('menuitem', { name: 'Delete message' }));
  return row.getAttribute('data-row-key') ?? '';
}

const byKey = (log: HTMLElement, key: string) => log.querySelector<HTMLElement>(`[data-row-key="${key}"]`);

describe('Focus around deleting', () => {
  it('Delete_CancelledWithTheButton_FocusesTheRow', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    const key = await askToDelete(log, 'message 2');

    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(byKey(log, key)).toHaveFocus();
    });
  });

  it('Delete_CancelledWithEscape_FocusesTheRow', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    const key = await askToDelete(log, 'message 2');

    fireEvent.keyDown(await screen.findByRole('dialog'), { key: 'Escape' });

    await waitFor(() => {
      expect(byKey(log, key)).toHaveFocus();
    });
  });

  it('Delete_Confirmed_KeepsTheRowAndItsFocusAsATombstone', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    const key = await askToDelete(log, 'message 2');

    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(byKey(log, key)).toHaveTextContent('This message was deleted');
    });
    expect(byKey(log, key)).toHaveFocus();
  });

  it('Delete_Refused_PutsTheRowBackWithTheSameKeyAndReadsTheChannelAgain', async () => {
    const api = fakeApi({ deleteMessage: vi.fn(() => Promise.reject(new ApiError(500, 'server.error'))) });
    renderWorkspace(api);
    const log = await openLog();
    const key = await askToDelete(log, 'message 2');
    const reads = vi.mocked(api.listMessages).mock.calls.length;

    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
    expect(byKey(log, key)).toHaveTextContent('message 2');
    await waitFor(() => {
      expect(vi.mocked(api.listMessages).mock.calls.length).toBeGreaterThan(reads);
    });
  });

  it('Delete_WhenTheServerSaysItIsAlreadyDeleted_KeepsTheTombstoneWithoutAnError', async () => {
    const api = fakeApi({
      deleteMessage: vi.fn(() => Promise.reject(new ApiError(409, 'messaging.message_deleted'))),
    });
    renderWorkspace(api);
    const log = await openLog();
    await askToDelete(log, 'message 2');

    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(api.deleteMessage).toHaveBeenCalledTimes(1);
    });
    await settle();
    expect(within(log).getByText('This message was deleted')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('Delete_ErrorAlert_CanBeDismissed', async () => {
    const api = fakeApi({
      deleteMessage: vi.fn(() => Promise.reject(new ApiError(429, 'messaging.rate_limited'))),
    });
    renderWorkspace(api);
    const log = await openLog();
    await askToDelete(log, 'message 2');
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));
    const alert = await screen.findByRole('alert');

    fireEvent.click(within(alert.parentElement as HTMLElement).getByRole('button', { name: 'Close' }));

    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('Editing and live changes', () => {
  it('Edit_WhenTheMessageIsDeletedLive_ClosesTheEditorAndFocusesTheTombstone', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();
    const row = rowOf(log, 'message 2');
    await openMenu(row);
    fireEvent.click(within(row).getByRole('menuitem', { name: 'Edit message' }));
    await screen.findByRole('textbox', { name: 'Edit message' });
    const key = row.getAttribute('data-row-key') ?? '';

    realtime.emit('MessageDeleted', deletedEvent(2, 30));

    await waitFor(() => {
      expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
    });
    expect(byKey(log, key)).toHaveTextContent('This message was deleted');
    await waitFor(() => {
      expect(byKey(log, key)).toHaveFocus();
    });
  });

  it('Edit_StartedFromTheComposer_TakesTheFocus', async () => {
    renderWorkspace(fakeApi());
    await openLog();
    const composer = await screen.findByRole('textbox', { name: 'Message #general' });

    fireEvent.keyDown(composer, { key: 'ArrowUp' });

    const input = await screen.findByRole<WithEditor>('textbox', { name: 'Edit message' });
    await waitFor(() => {
      expect(hasSelection(input)).toBe(true);
    });
  });
});

describe('MessageList editing rules', () => {
  const rows = (overlay = false): Row[] => [
    { key: 'k2', message: message(2), ...(overlay ? { overlay: true } : {}) },
  ];
  const editing = (takeFocus: boolean): MessageEditing => ({
    start: vi.fn(),
    cancel: vi.fn(),
    save: vi.fn(() => Promise.resolve(undefined)),
    remove: vi.fn(),
    takeFocus: vi.fn(() => takeFocus),
  });
  const list = (props: { rows: Row[]; editing: MessageEditing; editingId?: string }) => (
    <LocaleProvider locale="en">
      <MessageList
        channelName="general"
        rows={props.rows}
        hasEarlier={false}
        loadingEarlier={false}
        onLoadEarlier={vi.fn()}
        hasNewer={false}
        loadingNewer={false}
        onLoadNewer={vi.fn()}
        authorName={() => 'Veli'}
        ownAccountId={OWN_ID}
        ownName="Alice"
        locale="en"
        onRetry={vi.fn()}
        onDiscard={vi.fn()}
        onJoin={vi.fn()}
        editing={props.editing}
        editingId={props.editingId}
      />
    </LocaleProvider>
  );

  it('MoreMenu_OnAConfirmedMessageStillInThePendingOverlay_IsNotAMenu', async () => {
    render(list({ rows: rows(true), editing: editing(true) }));
    const log = await screen.findByRole('feed');

    const more = await hover(rowOf(log, 'message 2')).findByRole('button', { name: 'More' });

    expect(more).not.toHaveAttribute('aria-haspopup', 'menu');
  });

  it('Editor_StartedByThePerson_TakesTheFocus', async () => {
    render(list({ rows: rows(), editing: editing(true), editingId: 'm-2' }));

    const input = await screen.findByRole<WithEditor>('textbox', { name: 'Edit message' });

    await waitFor(() => {
      expect(hasSelection(input)).toBe(true);
    });
  });

  it('Editor_DrawnAgain_DoesNotTakeTheFocus', async () => {
    render(list({ rows: rows(), editing: editing(false), editingId: 'm-2' }));

    const input = await screen.findByRole<WithEditor>('textbox', { name: 'Edit message' });
    await settle();

    expect(hasSelection(input)).toBe(false);
  });
});
