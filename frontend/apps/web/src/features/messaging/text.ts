/** The longest message text, in UTF-16 characters after normalization (docs/contracts/phase-1.md, decision 12). */
export const MAX_MESSAGE_LENGTH = 4000;

/** Show how many characters are left once this few remain. */
export const COUNTER_FROM_REMAINING = 200;

/**
 * .NET's `char.IsWhiteSpace`, which the server's `Trim()` uses: U+0009 to U+000D, U+0085 and the Unicode space,
 * line and paragraph separators (Zs, Zl, Zp). It is not JavaScript's set: JS also trims U+FEFF, .NET does not.
 */
const DOTNET_WHITESPACE = String.raw`[\t-\r\u0085\p{Zs}\p{Zl}\p{Zp}]`;
const TRIM = new RegExp(`^${DOTNET_WHITESPACE}+|${DOTNET_WHITESPACE}+$`, 'gu');

/**
 * The text as the server stores it: trimmed the way .NET trims, NFC, line endings (`\r\n` and a lone `\r`) as `\n`.
 * The length is counted on this, so the composer's count is the server's.
 */
export function normalizeMessageText(raw: string): string {
  return raw.replace(TRIM, '').normalize('NFC').replaceAll('\r\n', '\n').replaceAll('\r', '\n');
}
