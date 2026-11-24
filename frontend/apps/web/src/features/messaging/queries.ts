import { useInfiniteQuery, type InfiniteData, type QueryClient } from '@tanstack/react-query';
import { rememberSeq } from './seq-memory';
import type { Message, MessagePage, MessagingApi } from './api';

// Explicit cache keys, per organization and channel.
export const channelsKey = (orgId: string) => ['orgs', orgId, 'channels'] as const;
export const membersKey = (orgId: string) => ['orgs', orgId, 'members'] as const;
export const messagesKey = (orgId: string, channelId: string) =>
  ['orgs', orgId, 'channels', channelId, 'messages'] as const;

export type MessagePages = InfiniteData<MessagePage, number | undefined>;

/** The channel's messages, newest page first; `fetchNextPage` loads the page before the oldest one. */
export function useChannelMessages(api: MessagingApi, orgId: string, channelId: string) {
  return useInfiniteQuery({
    queryKey: messagesKey(orgId, channelId),
    queryFn: async ({ pageParam, signal }) => {
      const page = await api.listMessages(orgId, channelId, pageParam, signal);
      const newest = page.messages[0];
      if (newest && pageParam === undefined) rememberSeq(orgId, channelId, newest.seq);
      return page;
    },
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
  });
}

/** Puts a message the server accepted into the newest page, unless it is already there (a refetch can be first). */
export function addMessageToCache(
  queryClient: QueryClient,
  orgId: string,
  channelId: string,
  message: Message,
): void {
  rememberSeq(orgId, channelId, message.seq);
  const key = messagesKey(orgId, channelId);
  if (!queryClient.getQueryData(key)) {
    return;
  }

  queryClient.setQueryData<MessagePages>(key, (data) => {
    const [first, ...rest] = data?.pages ?? [];
    if (!data || !first || data.pages.some((page) => page.messages.some(({ id }) => id === message.id))) {
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
