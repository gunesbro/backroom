import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import type { MessageDeleted, MessageEdited } from '@backroom/realtime';
import type { Message } from './api';
import {
  applyMessageChange,
  applyMessageDeleted,
  applyMessageEdited,
  deleteMessageOptimistically,
  restoreMessage,
} from './live-cache';
import { messagesKey, type MessagePages } from './queries';

const ORG = 'o';
const CHANNEL = 'c';

const original: Message = {
  id: 'm1',
  seq: 1,
  versionSeq: 1,
  clientMessageId: 'cm1',
  authorAccountId: 'a',
  text: 'first',
  createdAt: '2026-11-22T10:00:00Z',
};

function setup() {
  const queryClient = new QueryClient();
  const data: MessagePages = {
    pages: [{ messages: [original], next: null }],
    pageParams: [undefined],
  };
  queryClient.setQueryData(messagesKey(ORG, CHANNEL), data);
  const read = () => queryClient.getQueryData<MessagePages>(messagesKey(ORG, CHANNEL))?.pages[0]?.messages[0];
  return { queryClient, read };
}

const edited = (versionSeq: number, text: string): MessageEdited => ({
  orgId: ORG,
  conversationId: CHANNEL,
  messageId: 'm1',
  versionSeq,
  changeSeq: versionSeq,
  blocks: [],
  text,
  editedAt: '2026-11-22T10:30:00Z',
  epoch: 'e',
});
const deleted = (versionSeq: number): MessageDeleted => ({
  orgId: ORG,
  conversationId: CHANNEL,
  messageId: 'm1',
  versionSeq,
  changeSeq: versionSeq,
  epoch: 'e',
});
const now = () => new Date('2026-11-22T11:00:00Z');

describe('Message changes in the cache', () => {
  it('ApplyMessageChange_AnswerOlderThanALiveEventAlreadyApplied_IsIgnored', () => {
    const { queryClient, read } = setup();
    applyMessageEdited(queryClient, edited(5, 'live'));

    applyMessageChange(queryClient, ORG, CHANNEL, { ...original, versionSeq: 3, text: 'answer' });

    expect(read()?.text).toBe('live');
    expect(read()?.versionSeq).toBe(5);
  });

  it('ApplyMessageChange_NewerAnswer_Replaces', () => {
    const { queryClient, read } = setup();

    applyMessageChange(queryClient, ORG, CHANNEL, { ...original, versionSeq: 3, text: 'answer' });

    expect(read()?.text).toBe('answer');
  });

  it('RestoreMessage_WhenALiveDeleteLandedBeforeTheDeleteFailed_IsSkipped', () => {
    const { queryClient, read } = setup();
    const replaced = deleteMessageOptimistically(queryClient, ORG, CHANNEL, 'm1', now);
    applyMessageDeleted(queryClient, deleted(5), now);

    restoreMessage(queryClient, ORG, CHANNEL, replaced as Message);

    expect(read()?.deletedAt).toBeTruthy();
    expect(read()?.versionSeq).toBe(5);
  });

  it('RestoreMessage_WhenNothingElseChanged_PutsTheMessageBack', () => {
    const { queryClient, read } = setup();
    const replaced = deleteMessageOptimistically(queryClient, ORG, CHANNEL, 'm1', now);

    restoreMessage(queryClient, ORG, CHANNEL, replaced as Message);

    expect(read()).toEqual(original);
  });

  it('OptimisticDelete_KeepsTheOldVersionSoTheLiveDeleteStillApplies', () => {
    const { queryClient, read } = setup();

    deleteMessageOptimistically(queryClient, ORG, CHANNEL, 'm1', now);
    expect(read()?.versionSeq).toBe(1);
    expect(read()?.deletedAt).toBe('2026-11-22T11:00:00.000Z');
    applyMessageDeleted(queryClient, deleted(5), now);

    expect(read()?.versionSeq).toBe(5);
  });

  it('OptimisticDelete_OfADeletedMessage_ReplacesNothing', () => {
    const { queryClient } = setup();
    deleteMessageOptimistically(queryClient, ORG, CHANNEL, 'm1', now);

    expect(deleteMessageOptimistically(queryClient, ORG, CHANNEL, 'm1', now)).toBeUndefined();
  });

  it('Tombstone_KeepsNothingButItsPlace', () => {
    const { queryClient, read } = setup();

    applyMessageDeleted(queryClient, deleted(5), now);

    expect(read()).toEqual({
      id: 'm1',
      seq: 1,
      versionSeq: 5,
      createdAt: original.createdAt,
      deletedAt: '2026-11-22T11:00:00.000Z',
      clientMessageId: '',
      authorAccountId: '',
      text: '',
    });
  });
});
