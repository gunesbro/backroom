import { type InfiniteData, type QueryClient } from '@tanstack/react-query';
import { channelsKey, messagesKey, type MessagePages } from './queries';
import { lastSeenSeq, rememberSeq } from './seq-memory';
import type { Channel, ChannelPage, Message, MessagingApi } from './api';

// Applying live events to the query cache. Live events are hints (ADR 0011): everything here is idempotent, and a
// missed event is repaired by `catchUpChannel`.

/** A reconnect catch-up reads at most this many pages (50 messages each); beyond that the channel is reloaded. */
export const MAX_CATCH_UP_PAGES = 10;

/**
 * Puts a message into the newest page in `seq` order (newest first), unless the page already has it by id or by
 * `clientMessageId` (the person's own optimistic send). Does nothing when the channel is not in the cache or its
 * newest page is not loaded; returns whether the message is in the cache afterwards. It never moves the position
 * used for catch-up (`lastSeenSeq`): live events can be dropped, so only reads of the server do that.
 */
export function applyLiveMessage(
  queryClient: QueryClient,
  orgId: string,
  channelId: string,
  message: Message,
): boolean {
  const result = { present: false };
  queryClient.setQueryData<MessagePages>(messagesKey(orgId, channelId), (data) => {
    const first = data?.pages[0];
    if (!data || !first || data.pageParams[0] !== undefined) return data;
    result.present = true;
    const known = data.pages.some((page) =>
      page.messages.some(
        ({ id, clientMessageId }) => id === message.id || clientMessageId === message.clientMessageId,
      ),
    );
    if (known) return data;
    const at = first.messages.findIndex(({ seq }) => seq < message.seq);
    const messages = [...first.messages];
    messages.splice(at === -1 ? messages.length : at, 0, message);
    return { ...data, pages: [{ ...first, messages }, ...data.pages.slice(1)] };
  });
  return result.present;
}

/** Adds a created channel to the list, in the page where it belongs (oldest first, so the last one). */
export function applyChannelCreated(queryClient: QueryClient, orgId: string, channel: Channel): void {
  queryClient.setQueryData<InfiniteData<ChannelPage, string | undefined>>(channelsKey(orgId), (data) => {
    const last = data?.pages.at(-1);
    if (!data || !last) return data;
    if (data.pages.some((page) => page.channels.some(({ id }) => id === channel.id))) return data;
    // More pages to load: the channel arrives with them, in order.
    if (last.next !== null) return data;
    return {
      ...data,
      pages: [...data.pages.slice(0, -1), { ...last, channels: [...last.channels, channel] }],
    };
  });
}

/**
 * Fills the gap after a reconnect (or a long time in the background): reads `after={lastSeenSeq}` until `next` is
 * null, at most `MAX_CATCH_UP_PAGES` pages; beyond that, or without a known position, the channel is reset to its
 * newest page. Never throws: a failed read leaves the cache as it was and returns false.
 */
export async function catchUpChannel(
  api: MessagingApi,
  queryClient: QueryClient,
  orgId: string,
  channelId: string,
  signal?: AbortSignal,
): Promise<boolean> {
  const queryKey = messagesKey(orgId, channelId);
  const data = queryClient.getQueryData<MessagePages>(queryKey);
  if (!data) return true; // Not loaded: opening the channel reads it.
  try {
    let cursor = lastSeenSeq(orgId, channelId);
    if (cursor === undefined || data.pageParams[0] !== undefined) {
      await queryClient.resetQueries({ queryKey });
      return true;
    }
    // A fetch already running could return a page without what this finds, and replace it.
    await queryClient.cancelQueries({ queryKey });
    for (let page = 0; page < MAX_CATCH_UP_PAGES; page += 1) {
      const result = await api.listMessages(orgId, channelId, { after: cursor }, signal);
      // Pages are newest first; apply oldest first so each one lands at the head.
      if (signal?.aborted) return false;
      for (const message of result.messages.toReversed())
        applyLiveMessage(queryClient, orgId, channelId, message);
      // Only a read of the server moves the position: a live event may follow a dropped one.
      const newest = result.messages[0];
      if (newest) rememberSeq(orgId, channelId, newest.seq);
      if (result.next === null) return true;
      if (result.next <= cursor) break;
      cursor = result.next;
    }
    if (signal?.aborted) return false;
    await queryClient.resetQueries({ queryKey });
    return true;
  } catch {
    return false;
  }
}
