import { useInfiniteQuery, useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query';
import { rememberSeq } from './seq-memory';
import { useCallback } from 'react';
import { ApiError } from '@/shared/http';
import type { ConversationPage, Message, MessageCursor, MessagePage, MessagingApi } from './api';

// Explicit cache keys, per organization and conversation. The conversations list has its own segment so that
// invalidating it never touches the messages of a conversation.
export const conversationsKey = (orgId: string) => ['orgs', orgId, 'conversations', 'list'] as const;
/** The public channels to browse, joined or not. */
export const browseChannelsKey = (orgId: string) => ['orgs', orgId, 'channels'] as const;
export const membersKey = (orgId: string) => ['orgs', orgId, 'members'] as const;
/** Everything cached about one conversation: its details, members and messages. */
export const conversationScopeKey = (orgId: string, conversationId: string) =>
  ['orgs', orgId, 'conversations', conversationId] as const;
export const conversationKey = (orgId: string, conversationId: string) =>
  ['orgs', orgId, 'conversations', conversationId, 'detail'] as const;
export const conversationMembersKey = (orgId: string, conversationId: string) =>
  ['orgs', orgId, 'conversations', conversationId, 'members'] as const;
/** The person's archived conversations, which the sidebar's list leaves out. */
export const archivedConversationsKey = (orgId: string) =>
  ['orgs', orgId, 'conversations', 'archived'] as const;
export const myPermissionsKey = (orgId: string) => ['orgs', orgId, 'me', 'permissions'] as const;
export const messagesKey = (orgId: string, conversationId: string) =>
  ['orgs', orgId, 'conversations', conversationId, 'messages'] as const;

export type ConversationPages = InfiniteData<ConversationPage, string | undefined>;

/** At most this many pages of the organization's members (100 each) are held, for names and for choosing people. */
export const MAX_MEMBER_PAGES = 5;

/** The organization's members, page by page; callers load further pages only while they need them. */
export function useOrganizationMembers(api: MessagingApi, orgId: string) {
  return useInfiniteQuery({
    queryKey: membersKey(orgId),
    queryFn: ({ pageParam, signal }) => api.listMembers(orgId, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
    staleTime: 5 * 60_000,
    maxPages: MAX_MEMBER_PAGES,
  });
}

/**
 * The person's own permission keys in the organization, to decide what to offer; the server checks every request
 * for itself. A role change shows after five minutes at the latest, or when the page is loaded again.
 */
export function useMyPermissions(api: MessagingApi, orgId: string, enabled = true) {
  return useQuery({
    queryKey: myPermissionsKey(orgId),
    queryFn: ({ signal }) => api.listMyPermissions(orgId, signal),
    staleTime: 5 * 60_000,
    enabled,
  });
}

/** One conversation: its kind, description, archive state, member count and the person's own role in it. */
export function useConversation(api: MessagingApi, orgId: string, conversationId: string) {
  return useQuery({
    queryKey: conversationKey(orgId, conversationId),
    queryFn: ({ signal }) => api.getConversation(orgId, conversationId, signal),
    staleTime: 60_000,
  });
}

/** At most this many pages of a channel stay in memory; reading further back drops the newest ones. */
export const MAX_MESSAGE_PAGES = 10;

export type MessagePages = InfiniteData<MessagePage, MessageCursor>;

/** The cursors that continue from a page carry its epoch, if it has one. */
const withEpoch = <T extends { before: number } | { after: number }>(cursor: T, page: MessagePage) =>
  page.epoch === undefined ? cursor : { ...cursor, epoch: page.epoch };

/**
 * The view of a conversation is detached from its newest messages: it is a window somewhere in its history (after a
 * jump to a message, or after reading so far back that the newest pages were dropped). Live messages are then not
 * put into it; going back to the newest messages reads them.
 */
export function isDetached(data: { pageParams: readonly unknown[] } | undefined): boolean {
  return data !== undefined && data.pageParams[0] !== undefined;
}

/** How a jump to a message ended: shown (it is in the list now), gone (it cannot be read), or failed (try again). */
export type JumpResult = 'shown' | 'gone' | 'failed';

/** The server's epoch is not the one of the cursors held (ADR 0011): the view starts over from the newest messages. */
export function isResyncRequired(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'messaging.resync_required';
}

/**
 * The channel's messages, newest page first: `fetchNextPage` loads the page before the oldest one. When the view is
 * detached (`isDetached`: pages were dropped by maxPages, or `jumpTo` replaced them with a window around a message),
 * `hasPreviousPage` is true and `fetchNewer` loads the page after the newest one held, until it reaches the newest
 * message and the view is attached again. A jump is one more page of the same query, so the cache stays bounded by
 * `MAX_MESSAGE_PAGES` and every change to cached messages (live edits and deletes, quotes) reaches whichever view is
 * shown.
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
    getNextPageParam: (last): MessageCursor =>
      last.next === null ? undefined : withEpoch({ before: last.next }, last),
    // The first page is newer than the channel's newest message only when it is not the initial one.
    getPreviousPageParam: (first, _all, firstParam): MessageCursor => {
      const newest = first.messages[0];
      return firstParam === undefined || !newest ? undefined : withEpoch({ after: newest.seq }, first);
    },
    maxPages: MAX_MESSAGE_PAGES,
  });

  /** Replaces the view with one page (nothing that runs may overwrite it), attached when its param is undefined. */
  const replaceWith = useCallback(
    async (page: MessagePage, param: MessageCursor) => {
      await queryClient.cancelQueries({ queryKey: messagesKey(orgId, channelId) });
      const newest = page.messages[0];
      if (newest && param === undefined) rememberSeq(orgId, channelId, newest.seq);
      queryClient.setQueryData<MessagePages>(messagesKey(orgId, channelId), {
        pages: [page],
        pageParams: [param],
      });
    },
    [queryClient, orgId, channelId],
  );

  /**
   * Back to the newest messages, as when the channel is opened: they are read again (messages that came while the
   * view was detached are among them). If the read fails, the channel is reloaded, which shows the error.
   */
  const returnToLatest = useCallback(async () => {
    try {
      await replaceWith(await api.listMessages(orgId, channelId, undefined), undefined);
    } catch {
      await queryClient.resetQueries({ queryKey: messagesKey(orgId, channelId) });
    }
  }, [api, orgId, channelId, queryClient, replaceWith]);

  /**
   * Replaces the view with the messages around this one (docs/contracts/phase-2.md, "Jump to a message"). The window
   * is attached when nothing is newer than it; otherwise it continues both ways with the answer's cursors and epoch.
   * Nothing changes when the read fails.
   */
  const jumpTo = useCallback(
    async (messageId: string): Promise<JumpResult> => {
      try {
        const page = await api.listMessages(orgId, channelId, { around: messageId });
        if (!page.messages.some(({ id }) => id === messageId)) return 'gone';
        await replaceWith(page, page.newest ? undefined : { around: messageId });
        return 'shown';
      } catch (error) {
        return error instanceof ApiError && error.code === 'messaging.message_not_found' ? 'gone' : 'failed';
      }
    },
    [api, orgId, channelId, replaceWith],
  );

  const { fetchPreviousPage } = query;
  /**
   * Loads the page after the newest one held. A page that reaches the channel's newest message attaches the view
   * again; an empty one cannot be rebuilt by a refetch (it has no cursor), so the newest messages are reloaded instead.
   */
  const fetchNewer = useCallback(async () => {
    const result = await fetchPreviousPage();
    const first = result.data?.pages[0];
    if (first && first.messages.length === 0) {
      await queryClient.resetQueries({ queryKey: messagesKey(orgId, channelId) });
      return;
    }

    const newest = first?.messages[0];
    if (!first?.newest || !newest) return;
    // The page is what a read of the newest messages would answer first: it becomes the initial one.
    rememberSeq(orgId, channelId, newest.seq);
    queryClient.setQueryData<MessagePages>(
      messagesKey(orgId, channelId),
      (data) => data && { ...data, pageParams: data.pageParams.with(0, undefined) },
    );
  }, [fetchPreviousPage, queryClient, orgId, channelId]);

  return { ...query, fetchNewer, jumpTo, returnToLatest, detached: isDetached(query.data) };
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
