// The newest sequence number seen per channel, for catching up after a reconnect (`after={seq}`, step 7b).
// In memory only: @backroom/local-store is not ready yet; the map is bounded.

const MAX_CHANNELS = 500;
const seen = new Map<string, number>();

const keyOf = (orgId: string, channelId: string) => `${orgId}/${channelId}`;

export function rememberSeq(orgId: string, channelId: string, seq: number): void {
  const key = keyOf(orgId, channelId);
  const previous = seen.get(key);
  if (previous !== undefined && previous >= seq) return;
  // Re-inserted last, so the least recently advanced channel is the one dropped.
  seen.delete(key);
  seen.set(key, seq);
  if (seen.size > MAX_CHANNELS) {
    const oldest = seen.keys().next();
    if (!oldest.done) seen.delete(oldest.value);
  }
}

export function lastSeenSeq(orgId: string, channelId: string): number | undefined {
  return seen.get(keyOf(orgId, channelId));
}

export function forgetSeqs(): void {
  seen.clear();
}
