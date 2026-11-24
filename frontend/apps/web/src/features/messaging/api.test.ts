import { afterEach, describe, expect, it, vi } from 'vitest';
import { createMessagingApi } from './api';

const message = (seq: number) => ({
  id: `m${String(seq)}`,
  seq,
  clientMessageId: `c${String(seq)}`,
  authorAccountId: 'a',
  text: String(seq),
  createdAt: '2026-11-22T10:00:00Z',
});

function answer(body: unknown) {
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify(body)));
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('createMessagingApi listMessages', () => {
  const api = createMessagingApi('https://cell.example.test', () => Promise.resolve('t'));

  it('ListMessages_WithAfter_ReturnsNewestFirstWithTheCursorOfThePageBelow', async () => {
    const fetchSpy = answer({ messages: [message(11), message(12), message(13)], next: 13 });

    const page = await api.listMessages('o', 'c', { after: 10 });

    expect(page.messages.map(({ seq }) => seq)).toEqual([13, 12, 11]);
    expect(page.next).toBe(11);
    expect(fetchSpy.mock.calls[0]?.[0] as string).toContain('after=10');
  });

  it('ListMessages_WithAfterAndNothingNewer_HasNoCursor', async () => {
    answer({ messages: [], next: null });

    const page = await api.listMessages('o', 'c', { after: 99 });

    expect(page).toEqual({ messages: [], next: null });
  });
});
