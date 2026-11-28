import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { Message, MessageCursor, MessagingApi } from './api';
import { MAX_MESSAGE_PAGES, useChannelMessages } from './queries';

const PAGE = 5;
const message = (seq: number): Message => ({
  id: `m${String(seq)}`,
  seq,
  clientMessageId: `c${String(seq)}`,
  authorAccountId: 'a',
  text: String(seq),
  createdAt: '2026-11-22T10:00:00Z',
});

/** A channel of 200 messages, `PAGE` a page, newest first. */
function fakeApi() {
  const listMessages = vi.fn<MessagingApi['listMessages']>((_org, _channel, cursor: MessageCursor) => {
    const seqs: number[] = [];
    if (cursor && 'after' in cursor) {
      for (let seq = Math.min(cursor.after + PAGE, 200); seq > cursor.after; seq -= 1) seqs.push(seq);
      // As createMessagingApi shapes it: newest first, `next` is the cursor of the page below (null when empty).
      return Promise.resolve({ messages: seqs.map(message), next: seqs.at(-1) ?? null });
    }

    const top = cursor && 'before' in cursor ? cursor.before - 1 : 200;
    for (let seq = top; seq >= 1 && seqs.length < PAGE; seq -= 1) seqs.push(seq);
    const last = seqs.at(-1) ?? 1;
    return Promise.resolve({ messages: seqs.map(message), next: last > 1 ? last : null });
  });
  return { api: { listMessages } as unknown as MessagingApi, listMessages };
}

describe('useChannelMessages', () => {
  it('Read_FurtherBackThanTheLimit_DropsTheNewestPagesAndLoadsThemBackWithAfter', async () => {
    const { api, listMessages } = fakeApi();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useChannelMessages(api, 'org', 'ch'), { wrapper });
    await waitFor(() => {
      expect(result.current.data?.pages).toHaveLength(1);
    });
    expect(result.current.hasPreviousPage).toBe(false);

    for (let page = 0; page < MAX_MESSAGE_PAGES + 2; page += 1) {
      await act(async () => {
        await result.current.fetchNextPage();
      });
      await waitFor(() => {
        expect(result.current.isFetching).toBe(false);
      });
    }

    expect(result.current.data?.pages).toHaveLength(MAX_MESSAGE_PAGES);
    // The newest pages were dropped: the first page no longer starts at the channel's newest message.
    const newest = result.current.data?.pages[0]?.messages[0]?.seq ?? 0;
    expect(newest).toBeLessThan(200);
    expect(result.current.hasPreviousPage).toBe(true);

    await act(async () => {
      await result.current.fetchNewer();
    });
    await waitFor(() => {
      expect(result.current.isFetching).toBe(false);
    });
    expect(listMessages).toHaveBeenLastCalledWith('org', 'ch', { after: newest }, expect.anything());
    expect(result.current.data?.pages[0]?.messages[0]?.seq).toBe(newest + PAGE);
  });

  it('Refetch_AfterPagingBackAndForward_KeepsOneContiguousRunWithoutDuplicates', async () => {
    const { api } = fakeApi();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useChannelMessages(api, 'org', 'ch'), { wrapper });
    await waitFor(() => {
      expect(result.current.data?.pages).toHaveLength(1);
    });
    for (let page = 0; page < MAX_MESSAGE_PAGES + 2; page += 1) {
      await act(async () => {
        await result.current.fetchNextPage();
      });
      await waitFor(() => {
        expect(result.current.isFetching).toBe(false);
      });
    }
    await act(async () => {
      await result.current.fetchNewer();
    });
    await waitFor(() => {
      expect(result.current.isFetching).toBe(false);
    });

    await act(async () => {
      await result.current.refetch();
    });

    const seqs = result.current.data?.pages.flatMap((page) => page.messages.map(({ seq }) => seq)) ?? [];
    expect(seqs.length).toBeGreaterThan(0);
    expect(new Set(seqs).size).toBe(seqs.length);
    seqs.slice(1).forEach((seq, index) => {
      expect(seq).toBe((seqs[index] ?? 0) - 1);
    });
  });

  it('FetchNewer_WhenNothingIsNewer_StartsOverFromTheNewestPage', async () => {
    const { api } = fakeApi();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useChannelMessages(api, 'org', 'ch'), { wrapper });
    await waitFor(() => {
      expect(result.current.data?.pages).toHaveLength(1);
    });
    for (let page = 0; page < MAX_MESSAGE_PAGES + 2; page += 1) {
      await act(async () => {
        await result.current.fetchNextPage();
      });
      await waitFor(() => {
        expect(result.current.isFetching).toBe(false);
      });
    }

    for (let step = 0; step < 20 && result.current.hasPreviousPage; step += 1) {
      await act(async () => {
        await result.current.fetchNewer();
      });
      await waitFor(() => {
        expect(result.current.isFetching).toBe(false);
      });
    }

    expect(result.current.hasPreviousPage).toBe(false);
    expect(result.current.data?.pages[0]?.messages[0]?.seq).toBe(200);
    expect(result.current.data?.pages.every((page) => page.messages.length > 0)).toBe(true);
  });
});
