/** The part of a message the cache needs; everything else is stored as given. */
export interface MessageLike {
  readonly id: string;
  /** Commit-ordered sequence number inside one conversation; id and seq are one-to-one. */
  readonly seq: number;
  /** Version used when merging; defaults to seq. A copy with a lower version never replaces a higher one. */
  readonly versionSeq?: number;
  /** Set on a tombstone (deleted message). A tombstone always wins and live content is never restored over it. */
  readonly deletedAt?: string | number | null;
}

export interface Cursors {
  /** Sync epoch of the cell; a different epoch means a full resync. */
  readonly epoch?: string;
  /** Opaque feed cursor of the cell. */
  readonly feedCursor?: string;
  /** Last seen seq per conversation id. */
  readonly conversationSeqs: Readonly<Record<string, number>>;
}

export interface CursorsPatch {
  readonly epoch?: string;
  readonly feedCursor?: string;
  /** Merged into the stored map. */
  readonly conversationSeqs?: Readonly<Record<string, number>>;
}

export interface Limits {
  /** Newest messages kept per conversation (lowest seq dropped first). */
  readonly maxMessagesPerConversation: number;
  /** Conversations kept per account; least recently used are evicted. */
  readonly maxConversations: number;
  /** Total budget, approximated by the serialized size (length of the JSON text). */
  readonly maxBytes: number;
  /** Largest single key-value entry; bigger values are not stored. */
  readonly maxKvValueBytes: number;
  /** Conversation cursors kept per cell; the oldest are dropped. */
  readonly maxConversationCursors: number;
}

export const defaultLimits: Limits = {
  maxMessagesPerConversation: 200,
  maxConversations: 100,
  maxBytes: 20 * 1024 * 1024,
  maxKvValueBytes: 256 * 1024,
  maxConversationCursors: 2000,
};

export type StoreKind = 'indexeddb' | 'memory';

export interface LocalStore<M extends MessageLike = MessageLike> {
  readonly kind: StoreKind;
  /** Cached messages ordered by seq, or undefined when the conversation is not cached. Counts as an access. */
  getMessages(cellId: string, conversationId: string): Promise<M[] | undefined>;
  /** Replaces the cached page of a conversation. */
  putPage(cellId: string, conversationId: string, messages: readonly M[]): Promise<void>;
  /** Merges by id (the given message wins) and keeps the result ordered by seq. */
  mergeMessages(cellId: string, conversationId: string, messages: readonly M[]): Promise<void>;
  getCursors(cellId: string): Promise<Cursors>;
  setCursors(cellId: string, patch: CursorsPatch): Promise<void>;
  /** Small key-value area, for example the conversation list. Values must be JSON-serializable; getKv returns unknown, so validate the shape. */
  getKv(cellId: string, key: string): Promise<unknown>;
  /** Returns false when the value is larger than the entry limit and was not stored. */
  setKv(cellId: string, key: string, value: unknown): Promise<boolean>;
  deleteKv(cellId: string, key: string): Promise<void>;
  /** Removes the cached messages and the seq cursor of a conversation (for example after removal from it). */
  purgeConversation(cellId: string, conversationId: string): Promise<void>;
  /** Removes everything cached for a cell. */
  purgeCell(cellId: string): Promise<void>;
  /** Deletes the whole database of this account (call on sign-out), also after a fallback to memory. */
  clear(): Promise<void>;
  close(): void;
}

export interface LocalStoreOptions {
  /** Opaque account id. Only a hash of it names the database. Never pass tokens. */
  readonly accountId: string;
  readonly limits?: Partial<Limits>;
  /** Stored data of another schema version is wiped. */
  readonly schemaVersion?: number;
  /** Defaults to the global `indexedDB`; null forces the in-memory store. */
  readonly indexedDB?: IDBFactory | null;
  /** Clock for the LRU order; defaults to `Date.now`. */
  readonly now?: () => number;
  /** Where the "purge pending" marker is kept (an account hash only); defaults to localStorage in a browser, null disables it. */
  readonly markerStorage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | null;
  /** Called once when the store falls back to memory. Carries no data. */
  readonly onFallback?: (reason: 'unavailable' | 'error') => void;
}
