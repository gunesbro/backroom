// Finds the emoji in a text, so they can be drawn as images while everything else stays text.

const MODIFIER = String.raw`[\u{1F3FB}-\u{1F3FF}]`;
// One pictograph, with a skin tone when it takes one or with the selector that asks for its emoji form; or a skin
// tone on its own.
const ELEMENT = String.raw`(?:\p{Emoji_Modifier_Base}${MODIFIER}|\p{Extended_Pictographic}\uFE0F?|${MODIFIER})`;
const SEQUENCE = [
  // A flag: two regional indicator letters.
  String.raw`\p{Regional_Indicator}{2}`,
  // A keycap: a digit, # or * with the keycap mark.
  String.raw`[0-9#*]\uFE0F?\u20E3`,
  // A flag of a part of a country: the black flag, tag letters and the closing tag.
  String.raw`\u{1F3F4}[\u{E0020}-\u{E007E}]+\u{E007F}`,
  // Pictographs, joined by zero width joiners into one (a family, a profession, a flag with a symbol).
  String.raw`${ELEMENT}(?:\u200D${ELEMENT})*`,
].join('|');

// A sequence that is followed by the text presentation selector is text.
const EMOJI = new RegExp(String.raw`(?:${SEQUENCE})(?!\uFE0E)`, 'gu');
const EMOJI_PRESENTATION = /^\p{Emoji_Presentation}$/u;

/**
 * Whether a match is shown as an emoji. A single character is, when Unicode shows it as an emoji by default: 😀
 * is, while ©, ™, ↔ or ♥ are text until the emoji selector follows them (♥\uFE0F). Sequences always are.
 */
function isEmoji(match: string): boolean {
  const first = match.codePointAt(0) ?? 0;
  const single = match.length === (first > 0xffff ? 2 : 1);
  return !single || EMOJI_PRESENTATION.test(match);
}

export interface EmojiMatch {
  readonly emoji: string;
  readonly start: number;
  readonly end: number;
}

/** The first emoji of the text, with where it is. */
export function firstEmoji(text: string): EmojiMatch | undefined {
  for (const match of text.matchAll(EMOJI)) {
    if (isEmoji(match[0])) return { emoji: match[0], start: match.index, end: match.index + match[0].length };
  }
  return undefined;
}

export type TextPart = string | { readonly emoji: string };

/** The text in order as plain text and emoji. A text without emoji is one part. */
export function splitEmoji(text: string): TextPart[] {
  const parts: TextPart[] = [];
  let at = 0;
  for (const match of text.matchAll(EMOJI)) {
    if (!isEmoji(match[0])) continue;
    if (match.index > at) parts.push(text.slice(at, match.index));
    parts.push({ emoji: match[0] });
    at = match.index + match[0].length;
  }
  if (at < text.length) parts.push(text.slice(at));
  return parts;
}

/** The most emoji a text of only emoji may have to be shown large. */
export const MAX_LARGE_EMOJI = 12;

/** Whether the text is nothing but a few emoji (and spaces between them); such a message shows them large. */
export function isEmojiOnly(text: string): boolean {
  // A long text is not looked at: that many emoji with a space each fit in this (the longest emoji is 15 units).
  if (text.length > MAX_LARGE_EMOJI * 16) return false;
  const parts = splitEmoji(text);
  const emoji = parts.filter((part) => typeof part !== 'string').length;
  return (
    emoji > 0 &&
    emoji <= MAX_LARGE_EMOJI &&
    parts.every((part) => typeof part !== 'string' || part.trim() === '')
  );
}
