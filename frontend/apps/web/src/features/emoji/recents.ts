import { readItem, writeItem } from '@/shared/storage';

// Per-viewer conveniences in local storage; the picker works without them.
const RECENT_KEY = 'ph.emoji.recent';
const TONE_KEY = 'ph.emoji.skin-tone';

/** Three rows of the picker. */
export const MAX_RECENT = 24;

/** No emoji is longer than this in UTF-16 units (the server's limit for one emoji). */
const MAX_EMOJI_LENGTH = 32;

/** The emoji picked last, newest first. Anything that is not such a list is ignored. */
export function readRecent(): string[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(readItem('local', RECENT_KEY) ?? '[]');
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) return [];
  const emojis = parsed.filter(
    (item): item is string => typeof item === 'string' && item !== '' && item.length <= MAX_EMOJI_LENGTH,
  );
  return [...new Set(emojis)].slice(0, MAX_RECENT);
}

/** Puts an emoji first in the list and returns the new list. */
export function addRecent(emoji: string): string[] {
  const next = [emoji, ...readRecent().filter((item) => item !== emoji)].slice(0, MAX_RECENT);
  writeItem('local', RECENT_KEY, JSON.stringify(next));
  return next;
}

/** 0 is the default (yellow); 1 to 5 run from light to dark. */
export type SkinTone = 0 | 1 | 2 | 3 | 4 | 5;

export function readSkinTone(): SkinTone {
  const value = Number(readItem('local', TONE_KEY) ?? '0');
  return value === 1 || value === 2 || value === 3 || value === 4 || value === 5 ? value : 0;
}

export function writeSkinTone(tone: SkinTone): void {
  writeItem('local', TONE_KEY, tone === 0 ? undefined : String(tone));
}
