import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import type { LexicalEditor } from 'lexical';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionStatus, RealtimeClient, RealtimeEventName, RealtimeEvents } from '@backroom/realtime';
import { AuthProvider } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { MessagingApiContext, type Message, type MessagingApi } from './api';
import { RealtimeContext } from './realtime-context';
import type { Block } from './rich-text/blocks';
import { $readBlocks, $writeBlocks } from './rich-text/lexical-blocks';
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

const ORG = 'org-1';
const OWN_ID = '00000000-0000-4000-8000-000000000001';
const OTHER_ID = '00000000-0000-4000-8000-000000000002';
const GENERAL = { id: 'c-general', name: 'general', createdAt: '2026-11-20T09:00:00Z' };

type WithEditor = HTMLElement & { __lexicalEditor?: LexicalEditor };

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

function fakeApi(extra: Partial<MessagingApi> = {}, count = 4) {
  const newest = Array.from({ length: count }, (_, index) => message(count - index));
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
  await import('./message-editor');
});

beforeEach(() => {
  flags.edit = true;
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

/** The message list once the signed-in person is known (their messages are told from the others by it). */
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

/** Puts the pointer over a row and returns queries within it (its action bar is then built). */
function hover(row: HTMLElement) {
  fireEvent.pointerEnter(row, { pointerType: 'mouse' });
  return within(row);
}

/** Opens the "more" menu of a row. */
async function openMenu(row: HTMLElement) {
  const more = await hover(row).findByRole('button', { name: 'More' });
  fireEvent.click(more);
  return within(row).getByRole('menu', { name: 'More' });
}

async function startEditing(log: HTMLElement, text: string) {
  const row = rowOf(log, text);
  await openMenu(row);
  fireEvent.click(within(row).getByRole('menuitem', { name: 'Edit message' }));
  const input = await screen.findByRole<WithEditor>('textbox', { name: 'Edit message' });
  const editor = input.__lexicalEditor as LexicalEditor;
  const write = (blocks: Block[]) => {
    act(() => {
      editor.update(
        () => {
          $writeBlocks(blocks);
        },
        { discrete: true },
      );
    });
  };
  return { row, input, editor, write, blocks: () => editor.read($readBlocks) };
}

const live = {
  edited: (
    seq: number,
    versionSeq: number,
    text: string,
    extra: Partial<RealtimeEvents['MessageEdited']> = {},
  ): RealtimeEvents['MessageEdited'] => ({
    orgId: ORG,
    conversationId: GENERAL.id,
    messageId: `m-${String(seq)}`,
    versionSeq,
    changeSeq: versionSeq,
    blocks: paragraph(text),
    text,
    editedAt: '2026-11-22T10:30:00Z',
    epoch: 'e1',
    ...extra,
  }),
  deleted: (seq: number, versionSeq: number): RealtimeEvents['MessageDeleted'] => ({
    orgId: ORG,
    conversationId: GENERAL.id,
    messageId: `m-${String(seq)}`,
    versionSeq,
    changeSeq: versionSeq,
    epoch: 'e1',
  }),
};

/** Lets what an event or a request started finish, so a negative check cannot pass just because it ran early. */
const settle = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });

const NO_CONTRAST = { 'color-contrast': { enabled: false }, region: { enabled: false } };

describe('Message menu', () => {
  it('MoreMenu_OnOwnMessage_OffersEditAndDelete', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();

    const menu = await openMenu(rowOf(log, 'message 2'));

    expect(
      within(menu)
        .getAllByRole('menuitem')
        .map((item) => item.textContent),
    ).toEqual(['Edit message', 'Delete message']);
  });

  it('MoreMenu_OnSomeoneElsesMessage_IsNotAMenu', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();

    const more = await hover(rowOf(log, 'message 3')).findByRole('button', { name: 'More' });

    expect(more).not.toHaveAttribute('aria-haspopup', 'menu');
  });

  it('MoreMenu_WhenTheFlagIsOff_IsNotAMenuEvenOnOwnMessages', async () => {
    flags.edit = false;
    renderWorkspace(fakeApi());
    const log = await openLog();

    const more = await hover(rowOf(log, 'message 2')).findByRole('button', { name: 'More' });

    expect(more).not.toHaveAttribute('aria-haspopup', 'menu');
    expect(screen.queryByRole('menuitem', { name: 'Edit message' })).toBeNull();
  });

  it('MoreMenu_OnAMessageWithAMention_OffersDeleteOnly', async () => {
    const mention = message(2, {
      blocks: [
        {
          type: 'paragraph',
          children: [
            { type: 'text', text: 'message 2' },
            { type: 'mention', accountId: OTHER_ID },
          ],
        },
      ],
    });
    renderWorkspace(
      fakeApi({ listMessages: () => Promise.resolve({ messages: [mention, message(1)], next: null }) }),
    );
    const log = await openLog();

    const menu = await openMenu(rowOf(log, 'message 2'));

    expect(
      within(menu)
        .getAllByRole('menuitem')
        .map((item) => item.textContent),
    ).toEqual(['Delete message']);
  });
});

describe('Editing a message', () => {
  it('Edit_SaveWithEnter_SendsTheBlocksAndShowsTheEditedMessage', async () => {
    const editMessage = vi.fn((_org: string, id: string, blocks: Block[]) =>
      Promise.resolve(
        message(2, {
          id,
          text: 'better text',
          blocks,
          versionSeq: 20,
          editedAt: '2026-11-22T10:30:00Z',
        }),
      ),
    );
    renderWorkspace(fakeApi({ editMessage }));
    const log = await openLog();
    const { input, write } = await startEditing(log, 'message 2');
    write(paragraph('better text'));

    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(editMessage).toHaveBeenCalledExactlyOnceWith(ORG, 'm-2', paragraph('better text'));
    });
    const row = await waitFor(() => rowOf(log, 'better text'));
    expect(row.textContent).toContain('· edited');
    expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
    // Focus is back on the row.
    await waitFor(() => {
      expect(rowOf(log, 'better text')).toHaveFocus();
    });
  });

  it('Edit_SaveWithTheButton_Saves', async () => {
    const editMessage = vi.fn((_org: string, _id: string, blocks: Block[]) =>
      Promise.resolve(message(2, { blocks, text: 'x', versionSeq: 20 })),
    );
    renderWorkspace(fakeApi({ editMessage }));
    const log = await openLog();
    const { write } = await startEditing(log, 'message 2');
    write(paragraph('x'));

    const save = screen.getByRole('button', { name: 'Save' });
    await waitFor(() => {
      expect(save).toBeEnabled();
    });
    fireEvent.click(save);

    await waitFor(() => {
      expect(editMessage).toHaveBeenCalledTimes(1);
    });
  });

  it('Edit_CancelButton_ClosesWithoutSaving', async () => {
    const api = fakeApi();
    renderWorkspace(api);
    const log = await openLog();
    const { write } = await startEditing(log, 'message 2');
    write(paragraph('changed my mind'));

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
    });
    expect(api.editMessage).not.toHaveBeenCalled();
    expect(rowOf(log, 'message 2')).toHaveFocus();
  });

  it('Edit_Escape_ClosesWithoutSavingAndReturnsFocusToTheRow', async () => {
    const api = fakeApi();
    renderWorkspace(api);
    const log = await openLog();
    const { input } = await startEditing(log, 'message 2');

    fireEvent.keyDown(input, { key: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
    });
    expect(api.editMessage).not.toHaveBeenCalled();
    expect(rowOf(log, 'message 2')).toHaveFocus();
  });

  it('Edit_ShiftEnter_DoesNotSave', async () => {
    const api = fakeApi();
    renderWorkspace(api);
    const log = await openLog();
    const { input, write } = await startEditing(log, 'message 2');
    write(paragraph('draft'));

    fireEvent.keyDown(input, { key: 'Enter', shiftKey: true });

    expect(api.editMessage).not.toHaveBeenCalled();
  });

  it('Edit_SavingUnchangedContent_JustCloses', async () => {
    const api = fakeApi();
    renderWorkspace(api);
    const log = await openLog();
    const { input } = await startEditing(log, 'message 2');

    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
    });
    expect(api.editMessage).not.toHaveBeenCalled();
  });

  it('Edit_StartsFilledWithTheMessage', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();

    const { blocks } = await startEditing(log, 'message 2');

    expect(blocks()).toEqual(paragraph('message 2'));
  });

  it('Edit_WhenTheServerRefuses_ShowsTheErrorAndKeepsTheEditor', async () => {
    const editMessage = vi.fn(() => Promise.reject(new ApiError(429, 'messaging.rate_limited', 30)));
    renderWorkspace(fakeApi({ editMessage }));
    const log = await openLog();
    const { input, write } = await startEditing(log, 'message 2');
    write(paragraph('again'));

    fireEvent.keyDown(input, { key: 'Enter' });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/too often/i);
    expect(screen.getByRole('textbox', { name: 'Edit message' })).toBeInTheDocument();
  });

  it('Edit_WhenTheMessageWasDeletedMeanwhile_ClosesAndShowsTheTombstone', async () => {
    const editMessage = vi.fn(() => Promise.reject(new ApiError(409, 'messaging.message_deleted')));
    renderWorkspace(fakeApi({ editMessage }));
    const log = await openLog();
    const { input, write } = await startEditing(log, 'message 2');
    write(paragraph('too late'));

    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(within(log).getAllByText('This message was deleted')).toHaveLength(1);
    });
    expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
  });

  it('ArrowUp_InTheEmptyComposer_EditsTheNewestOwnMessage', async () => {
    renderWorkspace(fakeApi());
    await openLog();
    const composer = await screen.findByRole('textbox', { name: 'Message #general' });

    fireEvent.keyDown(composer, { key: 'ArrowUp' });

    const input = await screen.findByRole<WithEditor>('textbox', { name: 'Edit message' });
    expect(input.__lexicalEditor?.read($readBlocks)).toEqual(paragraph('message 4'));
  });

  it('ArrowUp_InAComposerWithText_DoesNotStartAnEdit', async () => {
    renderWorkspace(fakeApi());
    await openLog();
    const composer = await screen.findByRole<WithEditor>('textbox', { name: 'Message #general' });
    act(() => {
      composer.__lexicalEditor?.update(
        () => {
          $writeBlocks(paragraph('typing'));
        },
        { discrete: true },
      );
    });

    fireEvent.keyDown(composer, { key: 'ArrowUp' });

    expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
  });

  it('ArrowUp_WhenTheFlagIsOff_DoesNotStartAnEdit', async () => {
    flags.edit = false;
    renderWorkspace(fakeApi());
    await openLog();
    const composer = await screen.findByRole('textbox', { name: 'Message #general' });

    fireEvent.keyDown(composer, { key: 'ArrowUp' });

    expect(screen.queryByRole('textbox', { name: 'Edit message' })).toBeNull();
  });

  it('Edit_WithTheEditorOpen_HasNoAccessibilityViolations', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();
    await startEditing(log, 'message 2');

    const results = await axe.run(log, { rules: NO_CONTRAST });

    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});

describe('Deleting a message', () => {
  const askToDelete = async (log: HTMLElement, text: string) => {
    const row = rowOf(log, text);
    await openMenu(row);
    fireEvent.click(within(row).getByRole('menuitem', { name: 'Delete message' }));
  };

  it('Delete_AsksFirst_AndCancelDeletesNothing', async () => {
    const api = fakeApi();
    renderWorkspace(api);
    const log = await openLog();

    await askToDelete(log, 'message 2');

    const dialog = await screen.findByRole('dialog', { name: 'Delete this message?' });
    expect(dialog).toHaveTextContent("The message is deleted for everyone. This can't be undone.");
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(api.deleteMessage).not.toHaveBeenCalled();
    expect(within(log).getByText('message 2')).toBeInTheDocument();
  });

  it('Delete_Confirmed_DeletesAndLeavesATombstoneInPlace', async () => {
    const api = fakeApi();
    renderWorkspace(api);
    const log = await openLog();

    await askToDelete(log, 'message 2');
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(api.deleteMessage).toHaveBeenCalledExactlyOnceWith(ORG, 'm-2');
    });
    await waitFor(() => {
      expect(within(log).getByText('This message was deleted')).toBeInTheDocument();
    });
    expect(within(log).queryByText('message 2')).toBeNull();
    // It keeps its place between its neighbours.
    const order = within(log)
      .getAllByRole('article')
      .map((item) => /message \d|This message was deleted/.exec(item.textContent)?.[0]);
    expect(order).toEqual(['message 1', 'This message was deleted', 'message 3', 'message 4']);
  });

  it('Delete_WhenTheServerRefuses_PutsTheMessageBackAndSaysWhy', async () => {
    const api = fakeApi({
      deleteMessage: vi.fn(() => Promise.reject(new ApiError(429, 'messaging.rate_limited'))),
    });
    renderWorkspace(api);
    const log = await openLog();

    await askToDelete(log, 'message 2');
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(/too often/i);
    });
    expect(within(log).getByText('message 2')).toBeInTheDocument();
    expect(within(log).queryByText('This message was deleted')).toBeNull();
  });

  it('Delete_DialogOpen_HasNoAccessibilityViolations', async () => {
    renderWorkspace(fakeApi());
    const log = await openLog();

    await askToDelete(log, 'message 2');
    const dialog = await screen.findByRole('dialog');

    const results = await axe.run(dialog, { rules: NO_CONTRAST });
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});

describe('Rendering', () => {
  it('EditedMessage_ShowsTheLabelAfterTheTime', async () => {
    renderWorkspace(
      fakeApi({
        listMessages: () =>
          Promise.resolve({
            messages: [message(3, { editedAt: '2026-11-22T10:15:00Z' }), message(1)],
            next: null,
          }),
      }),
    );
    const log = await openLog();

    expect(rowOf(log, 'message 3').querySelector('time')?.parentElement?.textContent).toMatch(/· edited$/);
    expect(rowOf(log, 'message 1').textContent).not.toContain('edited');
  });

  it('Tombstone_HasNoAuthorAvatarOrActionBar', async () => {
    const tombstone: Message = {
      id: 'm-3',
      seq: 3,
      versionSeq: 9,
      clientMessageId: '',
      authorAccountId: '',
      text: '',
      createdAt: '2026-11-22T10:00:00Z',
      deletedAt: '2026-11-22T10:20:00Z',
    };
    renderWorkspace(
      fakeApi({ listMessages: () => Promise.resolve({ messages: [tombstone, message(1)], next: null }) }),
    );
    const log = await openLog();

    const text = within(log).getByText('This message was deleted');
    const row = text.closest<HTMLElement>('[role="article"]') as HTMLElement;
    expect(text).toHaveClass('bm-tombstone');
    expect(row.querySelector('b')).toBeNull();
    expect(row.querySelector('.ph-avatar')).toBeNull();
    fireEvent.pointerEnter(row, { pointerType: 'mouse' });
    act(() => {
      row.focus();
    });
    expect(within(row).queryAllByRole('button')).toHaveLength(0);
    expect(row).toHaveAccessibleName('This message was deleted');
  });
});

describe('Live edits and deletes', () => {
  it('MessageEdited_UpdatesTheCachedMessageAndMarksItEdited', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();

    realtime.emit('MessageEdited', live.edited(3, 30, 'message 3 revised'));

    await waitFor(() => {
      expect(within(log).getByText('message 3 revised')).toBeInTheDocument();
    });
    expect(rowOf(log, 'message 3 revised').textContent).toContain('· edited');
  });

  it('MessageEdited_NotNewerThanTheCachedVersion_IsIgnored', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();
    realtime.emit('MessageEdited', live.edited(3, 30, 'message 3 second'));
    await waitFor(() => {
      expect(within(log).getByText('message 3 second')).toBeInTheDocument();
    });

    realtime.emit('MessageEdited', live.edited(3, 29, 'message 3 first'));
    realtime.emit('MessageEdited', live.edited(3, 30, 'message 3 again'));
    await settle();

    expect(within(log).getByText('message 3 second')).toBeInTheDocument();
    expect(within(log).queryByText('message 3 first')).toBeNull();
    expect(within(log).queryByText('message 3 again')).toBeNull();
  });

  it('MessageDeleted_TurnsTheMessageIntoATombstone', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();

    realtime.emit('MessageDeleted', live.deleted(3, 30));

    await waitFor(() => {
      expect(within(log).getByText('This message was deleted')).toBeInTheDocument();
    });
    expect(within(log).queryByText('message 3')).toBeNull();
  });

  it('MessageDeleted_NotNewerThanTheCachedVersion_IsIgnored', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();

    realtime.emit('MessageDeleted', live.deleted(3, 2));
    await settle();

    expect(within(log).getByText('message 3')).toBeInTheDocument();
    expect(within(log).queryByText('This message was deleted')).toBeNull();
  });

  it('MessageEdited_AfterADelete_IsIgnored', async () => {
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();
    realtime.emit('MessageDeleted', live.deleted(3, 30));
    await waitFor(() => {
      expect(within(log).getByText('This message was deleted')).toBeInTheDocument();
    });

    // Newer than the delete: only the tombstone itself keeps it out.
    realtime.emit('MessageEdited', live.edited(3, 40, 'back from the dead'));
    await settle();

    expect(within(log).queryByText('back from the dead')).toBeNull();
  });

  it('LiveEvents_WithTheFlagOff_StillShowEditsAndTombstones', async () => {
    flags.edit = false;
    const realtime = fakeRealtime();
    renderWorkspace(fakeApi(), realtime.client);
    const log = await openLog();

    realtime.emit('MessageEdited', live.edited(3, 30, 'message 3 revised'));
    realtime.emit('MessageDeleted', live.deleted(1, 31));

    await waitFor(() => {
      expect(within(log).getByText('message 3 revised')).toBeInTheDocument();
    });
    expect(within(log).getByText('This message was deleted')).toBeInTheDocument();
  });
});
