import { createContext, useContext } from 'react';
import { requestJson, type AccessTokenSource } from '@/shared/http';
import type { Block } from './rich-text/blocks';

/** The kinds of conversation (docs/contracts/phase-2.md, flow 1); the sidebar lists channels only so far. */
export type ConversationKind = 'public_channel' | 'private_channel' | 'dm' | 'group_dm' | 'thread';
/** `kind` is missing only in data made before private channels: such a channel is public. */
export type Channel = {
  id: string;
  name: string;
  createdAt: string;
  kind?: ConversationKind;
  description?: string | null;
};
/** One conversation as its reader sees it. `role` is the reader's own (null without a membership). */
export type ConversationDetail = {
  id: string;
  kind: ConversationKind;
  name: string | null;
  description: string | null;
  createdAt: string;
  archivedAt: string | null;
  memberCount: number;
  /** Only a private channel has an owner. */
  ownerAccountId: string | null;
  role: 'owner' | 'member' | null;
};
export type ConversationMember = { accountId: string; role: 'owner' | 'member'; joinedAt: string };
export type ConversationMemberPage = { members: ConversationMember[]; next: string | null };
export type NewChannel = {
  name: string;
  private?: boolean;
  description?: string;
  /** At most 50 other active members of the organization. */
  memberAccountIds?: readonly string[];
};

/** The most people one request may add to a channel (also when it is created). */
export const MAX_ADDED_MEMBERS = 50;
/** One page of public channels to browse (not only the ones the person is in). */
export type ChannelPage = { channels: Channel[]; next: string | null };
/** One page of the person's own conversations (their open memberships). */
export type ConversationPage = { conversations: Channel[]; next: string | null };
export type Message = {
  id: string;
  /** The channel's sequence number: messages are ordered and paged by it, never by id or time. */
  seq: number;
  clientMessageId: string;
  authorAccountId: string;
  /** Plain text. The server also derives `blocks`; Phase 1 has one paragraph, so the text is all that is read. */
  text: string;
  /** The rich text blocks (docs/contracts/phase-2.md). Unchecked server data: read it with `parseBlocks`. */
  blocks?: unknown;
  createdAt: string;
  /**
   * The number of the message's latest change (an edit or the delete); a change is applied only if its number is
   * higher than this one. The server always sends it; it is missing only in older fixtures, and counts as 0.
   */
  versionSeq?: number | undefined;
  /** When the message was last edited (ISO 8601); null or missing: never. */
  editedAt?: string | null | undefined;
  /**
   * Set on a tombstone (ADR 0011): a deleted message keeps only its place. `clientMessageId`, `authorAccountId` and
   * `text` are then empty strings, and `blocks` is missing.
   */
  deletedAt?: string | null | undefined;
};

/** A message as the server sends it: a tombstone has null instead of its content. */
type WireMessage = Omit<Message, 'clientMessageId' | 'authorAccountId' | 'text'> & {
  clientMessageId: string | null;
  authorAccountId: string | null;
  text: string | null;
};

/** Reads a message of the wire into ours: a tombstone loses everything but its place, whatever else it carries. */
export function readMessage(raw: WireMessage): Message {
  if (!raw.deletedAt) return raw as Message;
  return {
    id: raw.id,
    seq: raw.seq,
    versionSeq: raw.versionSeq,
    createdAt: raw.createdAt,
    deletedAt: raw.deletedAt,
    clientMessageId: '',
    authorAccountId: '',
    text: '',
  };
}
/** Where a page starts: the newest messages (none), earlier than a seq, or later than a seq. */
export type MessageCursor = { before: number } | { after: number } | undefined;
export type MessagePage = { messages: Message[]; next: number | null };
export type Member = { accountId: string; displayName: string; email: string | null };
export type MemberPage = { members: Member[]; next: string | null };

/** Page size asked for; the server allows 1 to 100. */
export const PAGE_SIZE = 50;

/** A cell's conversation, channel and message endpoints (docs/contracts/phase-2.md, "HTTP APIs", Messaging). */
export interface MessagingApi {
  /** The person's own conversations: what the sidebar lists. Private conversations appear only to their members. */
  listConversations: (
    orgId: string,
    after: string | undefined,
    signal?: AbortSignal,
    /** `archived`: only the person's archived conversations instead, which the plain list leaves out. */
    options?: { archived?: boolean },
  ) => Promise<ConversationPage>;
  /** Public channels of the organization, joined or not: the browse list. */
  browseChannels: (orgId: string, after: string | undefined, signal?: AbortSignal) => Promise<ChannelPage>;
  createChannel: (orgId: string, channel: NewChannel) => Promise<Channel>;
  /** One conversation the person may read; anything else answers `404 messaging.conversation_not_found`. */
  getConversation: (
    orgId: string,
    conversationId: string,
    signal?: AbortSignal,
  ) => Promise<ConversationDetail>;
  /** Renames or describes a channel (its owner, or `channels.manage-public`); an empty description removes it. */
  updateConversation: (
    orgId: string,
    conversationId: string,
    change: { name?: string; description?: string },
  ) => Promise<ConversationDetail>;
  archiveConversation: (orgId: string, conversationId: string) => Promise<void>;
  unarchiveConversation: (orgId: string, conversationId: string) => Promise<void>;
  /** One page of a conversation's members, by account id. */
  listConversationMembers: (
    orgId: string,
    conversationId: string,
    after: string | undefined,
    signal?: AbortSignal,
  ) => Promise<ConversationMemberPage>;
  /** Adds 1 to 50 active members of the organization to a private channel; people already in it are skipped. */
  addConversationMembers: (
    orgId: string,
    conversationId: string,
    accountIds: readonly string[],
  ) => Promise<void>;
  /** Hands a private channel to one of its members (its owner only). */
  transferOwnership: (orgId: string, conversationId: string, accountId: string) => Promise<void>;
  /** The person's own permission keys in the organization (`GET /me/permissions`): what to offer, never a grant. */
  listMyPermissions: (orgId: string, signal?: AbortSignal) => Promise<string[]>;
  /** Joins a public channel; joining one the person is in succeeds. */
  joinConversation: (orgId: string, conversationId: string) => Promise<void>;
  /** Leaves a conversation (not a DM); leaving one the person is not in succeeds. */
  leaveConversation: (orgId: string, conversationId: string) => Promise<void>;
  /** Always newest first: `before` pages back (the `next` of the previous page), `after` pages forward. */
  listMessages: (
    orgId: string,
    conversationId: string,
    cursor: MessageCursor,
    signal?: AbortSignal,
  ) => Promise<MessagePage>;
  /** Idempotent by `clientMessageId`: a repeat answers with the stored message. */
  sendMessage: (
    orgId: string,
    conversationId: string,
    input: { clientMessageId: string; text: string; blocks?: Block[] },
  ) => Promise<Message>;
  /**
   * Replaces the content of the person's own message (`PATCH /messages/{id}`); answers the changed message. The
   * answer's `versionSeq` is the number the live event of the same edit carries.
   */
  editMessage: (orgId: string, messageId: string, blocks: Block[]) => Promise<Message>;
  /** Deletes a message for everyone (`DELETE /messages/{id}`): the author's own, or any with moderator permission. */
  deleteMessage: (orgId: string, messageId: string) => Promise<void>;
  /** One page of members, to put names to authors. */
  listMembers: (orgId: string, after: string | undefined, signal?: AbortSignal) => Promise<MemberPage>;
}

/** The cell's address is not one a token may be sent to. */
export class CellAddressError extends Error {
  constructor() {
    super('The cell address is not allowed.');
    this.name = 'CellAddressError';
  }
}

const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);

/**
 * Whether a build may send cell tokens over http to a loopback host: the development server, and the desktop local
 * check (Vite mode desktop-local, a bundled build against the AppHost on the same machine). Never any other build.
 */
export function allowsLoopbackHttpCells(env: { DEV: boolean; MODE: string }): boolean {
  return env.DEV || env.MODE === 'desktop-local';
}

export const LOOPBACK_HTTP_CELLS = allowsLoopbackHttpCells(import.meta.env);

/**
 * The origin of a cell address the control plane sent, or undefined if a token must not go there: https only, and
 * http only for a loopback host in a build that allows it. Only the origin is used, never a path or query.
 */
export function cellOrigin(cellUrl: string, allowLoopbackHttp: boolean): string | undefined {
  let url: URL;
  try {
    url = new URL(cellUrl);
  } catch {
    return undefined;
  }
  if (url.username || url.password) return undefined;
  if (url.protocol === 'https:') return url.origin;
  if (url.protocol === 'http:' && allowLoopbackHttp && LOOPBACK_HOSTS.has(url.hostname)) return url.origin;
  return undefined;
}

export function createMessagingApi(
  cellUrl: string,
  cellToken: AccessTokenSource,
  { allowLoopbackHttp = LOOPBACK_HTTP_CELLS }: { allowLoopbackHttp?: boolean } = {},
): MessagingApi {
  const origin = cellOrigin(cellUrl, allowLoopbackHttp);
  // Every request asks for its token first, so a refused address never receives one.
  const getAccessToken: AccessTokenSource = origin ? cellToken : () => Promise.reject(new CellAddressError());
  const org = (orgId: string) => `${origin ?? 'about:blank'}/api/v1/orgs/${encodeURIComponent(orgId)}`;
  const conversation = (orgId: string, conversationId: string) =>
    `${org(orgId)}/conversations/${encodeURIComponent(conversationId)}`;
  const message = (orgId: string, messageId: string) =>
    `${org(orgId)}/messages/${encodeURIComponent(messageId)}`;
  return {
    listConversations: async (orgId, after, signal, options) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (options?.archived) query.set('archived', 'true');
      if (after) query.set('after', after);
      const page = await requestJson<{ items: Channel[]; next: string | null }>(
        `${org(orgId)}/conversations?${query.toString()}`,
        getAccessToken,
        { signal },
      );
      return { conversations: page.items, next: page.next };
    },
    browseChannels: (orgId, after, signal) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (after) query.set('after', after);
      return requestJson(`${org(orgId)}/channels?${query.toString()}`, getAccessToken, { signal });
    },
    createChannel: (orgId, channel) =>
      requestJson(`${org(orgId)}/channels`, getAccessToken, { method: 'POST', body: channel }),
    getConversation: (orgId, conversationId, signal) =>
      requestJson(conversation(orgId, conversationId), getAccessToken, { signal }),
    updateConversation: (orgId, conversationId, change) =>
      requestJson(conversation(orgId, conversationId), getAccessToken, { method: 'PATCH', body: change }),
    archiveConversation: (orgId, conversationId) =>
      requestJson(`${conversation(orgId, conversationId)}/archive`, getAccessToken, { method: 'POST' }),
    unarchiveConversation: (orgId, conversationId) =>
      requestJson(`${conversation(orgId, conversationId)}/unarchive`, getAccessToken, { method: 'POST' }),
    listConversationMembers: async (orgId, conversationId, after, signal) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (after) query.set('after', after);
      const page = await requestJson<{ items: ConversationMember[]; next: string | null }>(
        `${conversation(orgId, conversationId)}/members?${query.toString()}`,
        getAccessToken,
        { signal },
      );
      return { members: page.items, next: page.next };
    },
    addConversationMembers: (orgId, conversationId, accountIds) =>
      requestJson(`${conversation(orgId, conversationId)}/members`, getAccessToken, {
        method: 'POST',
        body: { accountIds },
      }),
    transferOwnership: (orgId, conversationId, accountId) =>
      requestJson(`${conversation(orgId, conversationId)}/owner`, getAccessToken, {
        method: 'PUT',
        body: { accountId },
      }),
    listMyPermissions: async (orgId, signal) =>
      (
        await requestJson<{ permissions: string[] }>(`${org(orgId)}/me/permissions`, getAccessToken, {
          signal,
        })
      ).permissions,
    joinConversation: (orgId, conversationId) =>
      requestJson(`${conversation(orgId, conversationId)}/join`, getAccessToken, { method: 'POST' }),
    leaveConversation: (orgId, conversationId) =>
      requestJson(`${conversation(orgId, conversationId)}/leave`, getAccessToken, { method: 'POST' }),
    listMessages: async (orgId, conversationId, cursor, signal) => {
      const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (cursor && 'before' in cursor) query.set('before', String(cursor.before));
      if (cursor && 'after' in cursor) query.set('after', String(cursor.after));
      const raw = await requestJson<{ items: WireMessage[]; next: number | null }>(
        `${conversation(orgId, conversationId)}/messages?${query.toString()}`,
        getAccessToken,
        { signal },
      );
      const page: MessagePage = { messages: raw.items.map(readMessage), next: raw.next };
      if (!(cursor && 'after' in cursor)) return page;
      // `after` answers oldest first and its `next` points forward. Pages are kept newest first and a refetch rebuilds
      // the pages below the first one with `before={next}`, so `next` is made the cursor of the page below this one:
      // its oldest seq. An empty page has nothing newer, and nothing below it either.
      const newestFirst = page.messages.toReversed();
      return { messages: newestFirst, next: newestFirst.at(-1)?.seq ?? null };
    },
    sendMessage: async (orgId, conversationId, input) =>
      readMessage(
        await requestJson<WireMessage>(`${conversation(orgId, conversationId)}/messages`, getAccessToken, {
          method: 'POST',
          body: input,
        }),
      ),
    editMessage: async (orgId, messageId, blocks) =>
      readMessage(
        await requestJson<WireMessage>(message(orgId, messageId), getAccessToken, {
          method: 'PATCH',
          body: { blocks },
        }),
      ),
    deleteMessage: (orgId, messageId) =>
      requestJson(message(orgId, messageId), getAccessToken, { method: 'DELETE' }),
    listMembers: (orgId, after, signal) =>
      requestJson(
        `${org(orgId)}/members?limit=100${after ? `&after=${encodeURIComponent(after)}` : ''}`,
        getAccessToken,
        { signal },
      ),
  };
}

/** The cell an organization lives in. */
export type Cell = { id: string; url: string };

/** Builds the API of one cell; the app provides it, tests provide fakes or the real one against MSW. */
export type MessagingApiFactory = (cell: Cell) => MessagingApi;

/** A token for one cell (audience `cell-{id}`). Never the control plane's token: a cell must not receive it. */
export type CellTokenSource = (cellId: string) => Promise<string>;

/** Null while messaging is off (the feature flag): then no cell is called. */
export const MessagingApiContext = createContext<MessagingApiFactory | null>(null);

export function useMessagingEnabled(): boolean {
  return useContext(MessagingApiContext) !== null;
}

export function useMessagingApiFactory(): MessagingApiFactory {
  const factory = useContext(MessagingApiContext);
  if (!factory) {
    throw new Error('Messaging is used outside MessagingApiContext.');
  }

  return factory;
}
