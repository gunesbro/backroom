import { readItem, writeItem } from '@/shared/storage';
import { fitsSignIn } from './cell-tokens';

// Cells outside the shared list (customer-run, or registered after a sign-in) that this device needed, so the next
// sign-in asks for their scopes too (contract, "Remembered cells"). They are hints that grant nothing. Kept in local
// storage on the web and in the desktop app's web view, which is the app's own profile.

const KEY = 'ph.auth.remembered-cells';
export const MAX_REMEMBERED_CELLS = 10;
const FORGET_AFTER_MS = 90 * 24 * 60 * 60 * 1000;
/** Use is written at most once a day, not on every token. */
const TOUCH_EVERY_MS = 24 * 60 * 60 * 1000;

type Entries = Record<string, number>;

function read(now: number): Entries {
  let parsed: unknown;
  try {
    parsed = JSON.parse(readItem('local', KEY) ?? '{}');
  } catch {
    return {};
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return {};

  const entries = Object.entries(parsed as Record<string, unknown>)
    .filter(
      (entry): entry is [string, number] =>
        fitsSignIn(entry[0]) &&
        typeof entry[1] === 'number' &&
        entry[1] <= now &&
        now - entry[1] < FORGET_AFTER_MS,
    )
    .sort((a, b) => b[1] - a[1])
    .slice(0, MAX_REMEMBERED_CELLS);
  return Object.fromEntries(entries);
}

function write(entries: Entries): void {
  writeItem('local', KEY, Object.keys(entries).length ? JSON.stringify(entries) : undefined);
}

export type RememberedCells = {
  /** The cells used within 90 days, most recent first, at most 10. */
  list: () => string[];
  /** Remembers a cell; the least recently used one is dropped beyond 10. */
  remember: (cellId: string) => void;
  /** Records a use of a remembered cell, so it is kept for another 90 days. */
  touch: (cellId: string) => void;
  /** Forgets every cell (sign-out, or a sign-in that Keycloak refused with `invalid_scope`). */
  forget: () => void;
};

export function createRememberedCells(now: () => number = Date.now): RememberedCells {
  return {
    list: () => Object.keys(read(now())),
    remember: (cellId) => {
      if (!fitsSignIn(cellId)) return;
      const time = now();
      const entries = read(time);
      entries[cellId] = time;
      write(trim(entries));
    },
    touch: (cellId) => {
      const time = now();
      const entries = read(time);
      const last = entries[cellId];
      if (last === undefined || time - last < TOUCH_EVERY_MS) return;
      entries[cellId] = time;
      write(entries);
    },
    forget: () => {
      writeItem('local', KEY, undefined);
    },
  };
}

function trim(entries: Entries): Entries {
  return Object.fromEntries(
    Object.entries(entries)
      .sort((a, b) => b[1] - a[1])
      .slice(0, MAX_REMEMBERED_CELLS),
  );
}
