// Emoji whose image could not be loaded (an emoji newer than the image set, or no connection) and when: they are
// shown as text. Not for good: an image is tried again after a few minutes and once the connection is back.
// Bounded: past the limit the oldest entry goes.

export const MAX_MISSING = 500;
export const MISSING_FOR_MS = 5 * 60_000;

const missing = new Map<string, number>();

export function isMissingEmojiImage(emoji: string): boolean {
  const since = missing.get(emoji);
  if (since === undefined) return false;
  if (Date.now() - since < MISSING_FOR_MS) return true;
  missing.delete(emoji);
  return false;
}

export function addMissingEmojiImage(emoji: string): void {
  // Again at the end: the entries stay in the order of their time.
  missing.delete(emoji);
  if (missing.size >= MAX_MISSING) {
    const oldest = missing.keys().next();
    if (!oldest.done) missing.delete(oldest.value);
  }
  missing.set(emoji, Date.now());
}

/** Forgets which images could not be loaded, so each is tried again the next time it is drawn. */
export function forgetMissingEmojiImages(): void {
  missing.clear();
}

if (typeof window !== 'undefined') window.addEventListener('online', forgetMissingEmojiImages);
