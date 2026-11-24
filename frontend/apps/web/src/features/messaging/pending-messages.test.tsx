import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/shared/http';
import type { Message, MessagingApi } from './api';
import { usePendingMessages } from './pending-messages';
import type { Block } from './rich-text/blocks';

const stored = (input: { clientMessageId: string; text: string }): Message => ({
  id: 'm1',
  seq: 1,
  clientMessageId: input.clientMessageId,
  authorAccountId: 'own',
  text: input.text,
  createdAt: '2026-11-22T10:00:00Z',
});

function setup(sendMessage: MessagingApi['sendMessage']) {
  const api = { sendMessage } as MessagingApi;
  const queryClient = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return renderHook(() => usePendingMessages(api, 'org'), { wrapper });
}

describe('usePendingMessages', () => {
  it('Retry_CalledTwiceBeforeTheStateShowsSending_PostsOnce', async () => {
    const sendMessage = vi
      .fn<MessagingApi['sendMessage']>()
      .mockRejectedValueOnce(new ApiError(500, 'server.unexpected'))
      .mockImplementation((_org, _channel, input) => Promise.resolve(stored(input)));
    const { result } = setup(sendMessage);

    act(() => {
      result.current.send('c', 'hello');
    });
    await waitFor(() => {
      expect(result.current.forChannel('c')[0]?.status).toBe('failed');
    });
    const id = result.current.forChannel('c')[0]?.clientMessageId ?? '';

    // Two clicks handled before React re-rendered with the new status.
    await act(async () => {
      result.current.retry('c', id);
      result.current.retry('c', id);
      await Promise.resolve();
    });

    await waitFor(() => {
      expect(result.current.forChannel('c')[0]?.status).toBe('confirmed');
    });
    expect(sendMessage).toHaveBeenCalledTimes(2);
    expect(sendMessage.mock.calls[1]?.[2].clientMessageId).toBe(id);
  });

  it('Send_WhenNoBlocks_SendsNoBlocksField', async () => {
    const sendMessage = vi
      .fn<MessagingApi['sendMessage']>()
      .mockImplementation((_org, _channel, input) => Promise.resolve(stored(input)));
    const { result } = setup(sendMessage);

    act(() => {
      result.current.send('c', 'hello');
    });
    await waitFor(() => {
      expect(sendMessage).toHaveBeenCalledOnce();
    });

    expect(Object.keys(sendMessage.mock.calls[0]?.[2] ?? {}).sort()).toEqual(['clientMessageId', 'text']);
  });

  it('Retry_WhenSentWithBlocks_SendsTheSameBlocksAgain', async () => {
    const blocks: Block[] = [{ type: 'paragraph', children: [{ type: 'text', text: 'hello' }] }];
    const sendMessage = vi
      .fn<MessagingApi['sendMessage']>()
      .mockRejectedValueOnce(new ApiError(500, 'server.unexpected'))
      .mockImplementation((_org, _channel, input) => Promise.resolve(stored(input)));
    const { result } = setup(sendMessage);

    act(() => {
      result.current.send('c', 'hello', blocks);
    });
    await waitFor(() => {
      expect(result.current.forChannel('c')[0]?.status).toBe('failed');
    });
    act(() => {
      result.current.retry('c', result.current.forChannel('c')[0]?.clientMessageId ?? '');
    });
    await waitFor(() => {
      expect(sendMessage).toHaveBeenCalledTimes(2);
    });

    expect(sendMessage.mock.calls[1]?.[2].blocks).toEqual(blocks);
  });

  it('Prune_WhenAFetchedPageHasTheMessage_DropsTheConfirmedEntry', async () => {
    const { result } = setup((_org, _channel, input) => Promise.resolve(stored(input)));

    act(() => {
      result.current.send('c', 'hello');
    });
    await waitFor(() => {
      expect(result.current.forChannel('c')[0]?.status).toBe('confirmed');
    });
    act(() => {
      result.current.prune('c', new Set(['other']));
    });
    expect(result.current.forChannel('c')).toHaveLength(1);
    act(() => {
      result.current.prune('c', new Set(['m1']));
    });

    expect(result.current.forChannel('c')).toHaveLength(0);
  });
});
