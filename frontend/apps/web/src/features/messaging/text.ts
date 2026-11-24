/** The longest message text, in UTF-16 characters after normalization (docs/contracts/phase-1.md, decision 12). */
export const MAX_MESSAGE_LENGTH = 4000;

/** Show how many characters are left once this few remain. */
export const COUNTER_FROM_REMAINING = 200;

/**
 * The text as the server stores it: trimmed, NFC, line endings (`\r\n` and a lone `\r`) as `\n`. The length is
 * counted on this, so the composer's count is the server's.
 */
export function normalizeMessageText(raw: string): string {
  return raw.trim().normalize('NFC').replaceAll('\r\n', '\n').replaceAll('\r', '\n');
}
