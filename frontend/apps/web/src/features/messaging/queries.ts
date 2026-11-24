import { useInfiniteQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query';
import { rememberSeq } from './seq-memory';
import { useCallback } from 'react';
import type { Message, MessageCursor, MessagePage, MessagingApi } from './api';

// Explicit cache keys, per organization and conversation. The conversations list has its own segment so that
// invalidating it never touches the messages of a conversation.
export const conversationsKey = (orgId: string) => ['orgs', orgId, 'conversations', 'list'] as const;
/** The public channels to browse, joined or not. */
export const browseChannelsKey = (orgId: string) => ['orgs', orgId, 'channels'] as const;
export const membersKey = (orgId: string) => ['orgs', orgId, 'members'] as const;
export const messagesKey = (orgId: string, conversationId: string) =>
  ['orgs', orgId, 'conversations', conversationId, 'messages'] as const;

/** At most this many pages of a channel stay in memory; reading further back drops the newest ones. */
export const MAX_MESSAGE_PAGES = 10;

export type MessagePages = InfiniteData<MessagePage, MessageCursor>;

/**
 * The channel's messages, newest page first: `fetchNextPage` loads the page before the oldest one. When pages
 * were dropped (maxPages), `hasPreviousPage` is true and `fetchPreviousPage` loads the page after the newest one.
 */
export function useChannelMessages(api: MessagingApi, orgId: string, channelId: string) {
  const queryClient = useQueryClient();
  const query = useInfiniteQuery({
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

  const { fetchPreviousPage } = query;
  /** Loads the page after the newest one held; when nothing is newer, starts over from the channel's newest page. */
  const fetchNewer = useCallback(async () => {
    const result = await fetchPreviousPage();
    const first = result.data?.pages[0];
    // An empty page cannot be rebuilt by a refetch (it has no cursor): reload the newest messages instead.
    if (first && first.messages.length === 0) {
      await queryClient.resetQueries({ queryKey: messagesKey(orgId, channelId) });
    }
  }, [fetchPreviousPage, queryClient, orgId, channelId]);

  return { ...query, fetchNewer };
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
