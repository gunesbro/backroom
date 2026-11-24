// Local cache: IndexedDB on the web. A SQLite implementation for the desktop can follow
// the same Backend shape (backend.ts) without touching the cache logic.
export { createLocalStore, SCHEMA_VERSION } from './store.ts';
export { defaultLimits } from './types.ts';
export type {
  Cursors,
  CursorsPatch,
  Limits,
  LocalStore,
  LocalStoreOptions,
  MessageLike,
  StoreKind,
} from './types.ts';
