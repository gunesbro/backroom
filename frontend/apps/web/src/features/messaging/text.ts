/** The longest message text, in UTF-16 characters after normalization (docs/contracts/phase-1.md, decision 12). */
export const MAX_MESSAGE_LENGTH = 4000;

/** Show how many characters are left once this few remain. */
export const COUNTER_FROM_REMAINING = 200;

const SEPARATOR = /^[\p{Zs}\p{Zl}\p{Zp}]$/u;

/**
 * .NET's `char.IsWhiteSpace`, which the server's `Trim()` uses: U+0009 to U+000D, U+0085 and the Unicode space,
 * line and paragraph separators (Zs, Zl, Zp). It is not JavaScript's set: JS also trims U+FEFF, .NET does not.
 */
function isWhiteSpace(unit: string): boolean {
  const code = unit.charCodeAt(0);
  return (
    (code >= 0x09 && code <= 0x0d) || code === 0x20 || code === 0x85 || (code > 0x7f && SEPARATOR.test(unit))
  );
}

/** Trims in one pass from each end (a regular expression on a long run of spaces is quadratic). */
function trim(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && isWhiteSpace(text.charAt(start))) start += 1;
  while (end > start && isWhiteSpace(text.charAt(end - 1))) end -= 1;
  return text.slice(start, end);
}

/**
 * The text as the server stores it: trimmed the way .NET trims, NFC, line endings (`\r\n` and a lone `\r`) as `\n`.
 * The length is counted on this, so the composer's count is the server's.
 */
export function normalizeMessageText(raw: string): string {
  return trim(raw).normalize('NFC').replaceAll('\r\n', '\n').replaceAll('\r', '\n');
}

/** The longest text of a quote, in UTF-16 characters (the server cuts a quoted text there). */
export const MAX_QUOTE_LENGTH = 200;

const graphemes = new Intl.Segmenter('en', { granularity: 'grapheme' });

/**
 * At most `MAX_QUOTE_LENGTH` UTF-16 characters, cut back to a character boundary (never inside a surrogate pair or a
 * combining sequence), as the server cuts the quote it sends. Used where the client builds a quote itself.
 */
export function truncateQuote(text: string): string {
  if (text.length <= MAX_QUOTE_LENGTH) return text;
  let end = 0;
  for (const { segment } of graphemes.segment(text)) {
    if (end + segment.length > MAX_QUOTE_LENGTH) break;
    end += segment.length;
  }

  return text.slice(0, end);
}
