// Live events of the cell's hub (docs/contracts/phase-1.md and phase-2.md, "Live events"). Payloads are MessagePack
// maps with camelCase keys. Events are hints: the HTTP API stays the source of truth, and clients catch up by `seq`.

/** What a reply shows of the message it quotes: a snapshot of the moment it was posted (deleted: the id only). */
export type LiveQuote = {
  messageId: string;
  deleted: boolean;
  authorAccountId?: string | null | undefined;
  text?: string | null | undefined;
};

export type LiveMessage = {
  id: string;
  seq: number;
  clientMessageId: string;
  authorAccountId: string;
  text: string;
  /** The rich text blocks, when the event carries them (`MessagePosted.v2`). Unchecked: read with `parseBlocks`. */
  blocks?: unknown;
  createdAt: string;
  /** The quote, when the message is a reply. */
  replyTo?: LiveQuote | null | undefined;
};

export type MessagePosted = { orgId: string; channelId: string; message: LiveMessage };
export type ChannelCreated = { orgId: string; channel: { id: string; name: string; createdAt: string } };
export type SessionRevoked = { reason: string };
/** To the account's own group (docs/contracts/phase-2.md); `removed` also stops delivery of that conversation's events. */
export type MembershipChanged = { orgId: string; conversationId: string; change: 'added' | 'removed' };

/**
 * To the conversation's group: its current name, description, archive state and member count after a change (a rename,
 * an archive, a join or leave, an ownership transfer). It has no owner field: clients read the conversation again.
 */
export type ConversationUpdated = {
  orgId: string;
  conversationId: string;
  name: string | null;
  description: string | null;
  /** ISO 8601, or null while the conversation is open. */
  archivedAt: string | null;
  memberCount: number;
  changeSeq: number;
  epoch: string;
};

/**
 * To the conversation's group: a message was edited. It carries the new content; `versionSeq` is the message's new
 * change number, and a client ignores an event that is not newer than the version it has.
 */
export type MessageEdited = {
  orgId: string;
  conversationId: string;
  messageId: string;
  versionSeq: number;
  changeSeq: number;
  /** The new rich text blocks. Unchecked: read with `parseBlocks`. */
  blocks: unknown;
  text: string;
  /** ISO 8601. */
  editedAt: string;
  epoch: string;
};

/** To the conversation's group: a message was deleted. It carries no content; clients show the tombstone. */
export type MessageDeleted = {
  orgId: string;
  conversationId: string;
  messageId: string;
  versionSeq: number;
  changeSeq: number;
  epoch: string;
};

/** Automatic presence (docs/contracts/phase-2.md, flow 9); never platform or device. */
export type PresenceState = 'online' | 'idle' | 'offline';

/** A custom status: an emoji and/or a text of at most 100 characters, with an optional expiry (ISO 8601). */
export type CustomStatus = { emoji: string | null; text: string | null; expiresAt: string | null };

/** Sent to the watchers of a person (`presence:{accountId}` groups), at most one per person per 5 s. */
export type PresenceChanged = {
  orgId: string;
  accountId: string;
  state: PresenceState;
  /** ISO 8601; clients treat a past time as off. */
  dndUntil: string | null;
  customStatus: CustomStatus | null;
};

export type RealtimeEvents = {
  MessagePosted: MessagePosted;
  ChannelCreated: ChannelCreated;
  SessionRevoked: SessionRevoked;
  PresenceChanged: PresenceChanged;
  MembershipChanged: MembershipChanged;
  ConversationUpdated: ConversationUpdated;
  MessageEdited: MessageEdited;
  MessageDeleted: MessageDeleted;
};

export type RealtimeEventName = keyof RealtimeEvents;

export const EVENT_NAMES: readonly RealtimeEventName[] = [
  'MessagePosted',
  'ChannelCreated',
  'SessionRevoked',
  'PresenceChanged',
  'MembershipChanged',
  'ConversationUpdated',
  'MessageEdited',
  'MessageDeleted',
];

/** What a client reports about its person's input; the server takes one report per 10 s per connection. */
export type ActivityState = 'active' | 'idle';

/** The most people one connection may watch (`WatchPresence`). */
export const MAX_WATCHED_ACCOUNTS = 200;

/** `connecting`: first attempt; `reconnecting`: after a loss; `stopped`: closed on purpose or the session ended. */
export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'stopped';
