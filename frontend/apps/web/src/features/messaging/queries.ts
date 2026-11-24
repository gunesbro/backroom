import { useInfiniteQuery, type InfiniteData, type QueryClient } from '@tanstack/react-query';
import { rememberSeq } from './seq-memory';
import type { Message, MessageCursor, MessagePage, MessagingApi } from './api';

// Explicit cache keys, per organization and channel.
export const channelsKey = (orgId: string) => ['orgs', orgId, 'channels'] as const;
export const membersKey = (orgId: string) => ['orgs', orgId, 'members'] as const;
export const messagesKey = (orgId: string, channelId: string) =>
  ['orgs', orgId, 'channels', channelId, 'messages'] as const;

/** At most this many pages of a channel stay in memory; reading further back drops the newest ones. */
export const MAX_MESSAGE_PAGES = 10;

export type MessagePages = InfiniteData<MessagePage, MessageCursor>;

/**
 * The channel's messages, newest page first: `fetchNextPage` loads the page before the oldest one. When pages
 * were dropped (maxPages), `hasPreviousPage` is true and `fetchPreviousPage` loads the page after the newest one.
 */
export function useChannelMessages(api: MessagingApi, orgId: string, channelId: string) {
  return useInfiniteQuery({
    queryKey: messagesKey(orgId, channelId),
    queryFn: async ({ pageParam, signal }) => {
      const page = await api.listMessages(orgId, channelId, pageParam, signal);
      const newest = page.messages[0];
      if (newest && pageParam === undefined) rememberSeq(orgId, channelId, newest.seq);
      return page;
    },
    initialPageParam: undefined as MessageCursor,
    getNextPageParam: (last): MessageCursor => (last.next === null ? undefined : { before: last.next }),
    // The first page is newer than the channel's newest message only when it is not the initial one.
    getPreviousPageParam: (first, _all, firstParam): MessageCursor => {
      const newest = first.messages[0];
      return firstParam === undefined || !newest ? undefined : { after: newest.seq };
    },
    maxPages: MAX_MESSAGE_PAGES,
  });
}

/**
 * Puts a message the server accepted into the newest page, unless it is already there. Only when the newest page
 * is loaded: after pages were dropped the message is shown from the pending list until it is fetched.
 */
export function addMessageToCache(
  queryClient: QueryClient,
  orgId: string,
  channelId: string,
  message: Message,
): void {
  rememberSeq(orgId, channelId, message.seq);
  const key = messagesKey(orgId, channelId);
  queryClient.setQueryData<MessagePages>(key, (data) => {
    const [first, ...rest] = data?.pages ?? [];
    if (
      !data ||
      !first ||
      data.pageParams[0] !== undefined ||
      data.pages.some((page) => page.messages.some(({ id }) => id === message.id))
    ) {
      return data;
    }

    return { ...data, pages: [{ ...first, messages: [message, ...first.messages] }, ...rest] };
  });
}

/** The messages oldest first, as drawn. */
export function oldestFirst(data: { pages: readonly MessagePage[] } | undefined): Message[] {
  if (!data) return [];
  const result: Message[] = [];
  for (let page = data.pages.length - 1; page >= 0; page -= 1) {
    const messages = data.pages[page]?.messages ?? [];
    for (let index = messages.length - 1; index >= 0; index -= 1) {
      const message = messages[index];
      if (message) result.push(message);
    }
  }

  return result;
}
