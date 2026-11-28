import { type InfiniteData, type QueryClient } from '@tanstack/react-query';
import {
  archivedConversationsKey,
  browseChannelsKey,
  conversationKey,
  conversationMembersKey,
  conversationScopeKey,
  conversationsKey,
  messagesKey,
  type ConversationPages,
  type MessagePages,
} from './queries';
import { lastSeenSeq, rememberSeq } from './seq-memory';
import type { ConversationUpdated, MessageDeleted, MessageEdited } from '@backroom/realtime';
import type { ChannelPage, ConversationDetail, Message, MessagingApi } from './api';

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
    // A tombstone has no client message id: an empty one matches nothing.
    const known = data.pages.some((page) =>
      page.messages.some(
        ({ id, clientMessageId }) =>
          id === message.id || (clientMessageId !== '' && clientMessageId === message.clientMessageId),
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

/** Replaces the message with this id (if cached) by what `change` makes of it; nothing happens when it returns undefined. */
function changeMessage(
  queryClient: QueryClient,
  orgId: string,
  conversationId: string,
  messageId: string,
  change: (current: Message) => Message | undefined,
): void {
  queryClient.setQueryData<MessagePages>(messagesKey(orgId, conversationId), (data) => {
    if (!data) return data;
    for (const [pageIndex, page] of data.pages.entries()) {
      const at = page.messages.findIndex(({ id }) => id === messageId);
      const current = page.messages[at];
      if (!current) continue;
      const next = change(current);
      if (!next) return data;
      const messages = page.messages.with(at, next);
      return { ...data, pages: data.pages.with(pageIndex, { ...page, messages }) };
    }

    return data;
  });
}

const versionOf = (message: Message) => message.versionSeq ?? 0;

/** What a deleted message keeps: its place (ADR 0011, the tombstone). */
export function tombstoneOf(message: Message, versionSeq: number, deletedAt: string): Message {
  return {
    id: message.id,
    seq: message.seq,
    versionSeq,
    createdAt: message.createdAt,
    deletedAt,
    clientMessageId: '',
    authorAccountId: '',
    text: '',
  };
}

/**
 * The server's own copy of a changed message (the answer to an edit): it replaces the cached one only if it is
 * newer, so a live event that arrived first, or a later change, is never overwritten.
 */
export function applyMessageChange(
  queryClient: QueryClient,
  orgId: string,
  conversationId: string,
  message: Message,
): void {
  changeMessage(queryClient, orgId, conversationId, message.id, (current) =>
    versionOf(message) > versionOf(current) ? message : undefined,
  );
}

/** A live edit: new content, unless the event is not newer than the version the cache has (or the message is gone). */
export function applyMessageEdited(queryClient: QueryClient, event: MessageEdited): void {
  changeMessage(queryClient, event.orgId, event.conversationId, event.messageId, (current) =>
    event.versionSeq > versionOf(current) && !current.deletedAt
      ? {
          ...current,
          text: event.text,
          blocks: event.blocks,
          editedAt: event.editedAt,
          versionSeq: event.versionSeq,
        }
      : undefined,
  );
}

/** A live delete: the message becomes its tombstone, unless the event is not newer than the cache's version. */
export function applyMessageDeleted(
  queryClient: QueryClient,
  event: MessageDeleted,
  now: () => Date = () => new Date(),
): void {
  changeMessage(queryClient, event.orgId, event.conversationId, event.messageId, (current) =>
    event.versionSeq > versionOf(current)
      ? tombstoneOf(current, event.versionSeq, now().toISOString())
      : undefined,
  );
}

/**
 * Shows a message as deleted before the server has answered. The tombstone keeps the message's own version, so the
 * live event of the delete (a higher one) still applies. Returns the message that was replaced, to put back if the
 * delete is refused.
 */
export function deleteMessageOptimistically(
  queryClient: QueryClient,
  orgId: string,
  conversationId: string,
  messageId: string,
  now: () => Date = () => new Date(),
): Message | undefined {
  const result: { original?: Message } = {};
  changeMessage(queryClient, orgId, conversationId, messageId, (current) => {
    if (current.deletedAt) return undefined;
    result.original = current;
    return tombstoneOf(current, versionOf(current), now().toISOString());
  });
  return result.original;
}

/** Puts back what an optimistic delete replaced, unless the message has changed again since. */
export function restoreMessage(
  queryClient: QueryClient,
  orgId: string,
  conversationId: string,
  original: Message,
): void {
  changeMessage(queryClient, orgId, conversationId, original.id, (current) =>
    current.deletedAt && versionOf(current) === versionOf(original) ? original : undefined,
  );
}

/**
 * A channel was created: the browse list is out of date. Nobody but the creator is a member of it yet, so the
 * person's own conversations do not change.
 */
export function noteChannelCreated(queryClient: QueryClient, orgId: string): void {
  void queryClient.invalidateQueries({ queryKey: browseChannelsKey(orgId) });
}

/**
 * The person is no longer in a conversation, or may not read it any more: it leaves the sidebar's list and the list
 * of archived channels, and everything cached about it (details, members, messages) is dropped (docs/contracts/phase-2.md, "Removal").
 */
export function forgetConversation(queryClient: QueryClient, orgId: string, conversationId: string): void {
  queryClient.setQueryData<ConversationPages>(conversationsKey(orgId), (data) => {
    if (!data?.pages.some((page) => page.conversations.some(({ id }) => id === conversationId))) return data;
    return {
      ...data,
      pages: data.pages.map((page) => ({
        ...page,
        conversations: page.conversations.filter(({ id }) => id !== conversationId),
      })),
    };
  });
  // The archived list too: a private channel's name must not stay cached after the person lost it.
  queryClient.setQueryData<InfiniteData<ChannelPage, string | undefined>>(
    archivedConversationsKey(orgId),
    (data) => {
      if (!data?.pages.some((page) => page.channels.some(({ id }) => id === conversationId))) return data;
      return {
        ...data,
        pages: data.pages.map((page) => ({
          ...page,
          channels: page.channels.filter(({ id }) => id !== conversationId),
        })),
      };
    },
  );
  const queryKey = conversationScopeKey(orgId, conversationId);
  void queryClient.cancelQueries({ queryKey });
  queryClient.removeQueries({ queryKey });
}

/**
 * A conversation changed (name, description, archive state, member count). The event carries the new values, so the
 * caches are corrected in place and nothing is read again, except by a view that shows the owner and the members
 * (`refetch`): the event names neither. An archived conversation leaves the sidebar's list, as the server's list
 * leaves it out; one that came back is read again.
 */
export function applyConversationUpdated(
  queryClient: QueryClient,
  event: ConversationUpdated,
  refetch: boolean,
): void {
  const { orgId, conversationId, name, description, archivedAt, memberCount } = event;
  const found = { listed: false };
  queryClient.setQueryData<ConversationPages>(conversationsKey(orgId), (data) => {
    const current = data?.pages.flatMap((page) => page.conversations).find(({ id }) => id === conversationId);
    if (!data || !current) return data;
    found.listed = true;
    if (archivedAt === null && (name === null || name === current.name)) return data;
    return {
      ...data,
      pages: data.pages.map((page) => ({
        ...page,
        conversations:
          archivedAt === null
            ? page.conversations.map((item) =>
                item.id === conversationId && name !== null ? { ...item, name } : item,
              )
            : page.conversations.filter(({ id }) => id !== conversationId),
      })),
    };
  });
  if (!found.listed && archivedAt === null) {
    void queryClient.invalidateQueries({ queryKey: conversationsKey(orgId) });
  }

  // Read again only where the archived channels are on screen.
  void queryClient.invalidateQueries({ queryKey: archivedConversationsKey(orgId) });

  queryClient.setQueryData<ConversationDetail>(
    conversationKey(orgId, conversationId),
    (detail) => detail && { ...detail, name, description, archivedAt, memberCount },
  );
  void queryClient.invalidateQueries({
    queryKey: conversationKey(orgId, conversationId),
    refetchType: refetch ? 'active' : 'none',
  });
  void queryClient.invalidateQueries({ queryKey: conversationMembersKey(orgId, conversationId) });
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
