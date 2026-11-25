// What a channel's name and description may be, as the server counts them (docs/contracts/phase-2.md, flow 1).

export const MAX_NAME_CHARACTERS = 80;
/** The name input never takes more than this many UTF-16 units; the check below counts characters people see. */
export const MAX_NAME_UNITS = 800;
export const MAX_DESCRIPTION_CHARACTERS = 250;
/** Four code points per character at most, as the database bounds the column. */
export const MAX_DESCRIPTION_UNITS = 2000;

function characters(text: string): number {
  return Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)).length;
}

/** The text as it is sent: trimmed and NFC. */
export function normalizeText(raw: string): string {
  return raw.trim().normalize('NFC');
}

/** True when the name is not 1 to 80 user-perceived characters (trimmed, NFC). */
export function nameProblem(raw: string): boolean {
  const name = normalizeText(raw);
  return name === '' || characters(name) > MAX_NAME_CHARACTERS;
}

/** True when the description is longer than 250 user-perceived characters (trimmed, NFC); it may be empty. */
export function descriptionProblem(raw: string): boolean {
  return characters(normalizeText(raw)) > MAX_DESCRIPTION_CHARACTERS;
}

/**
 * Whether another channel the person can see has this name. Private channel names are not unique and the server
 * never compares them, so the client warns (docs/contracts/phase-2.md, "Channel names"). Names are not words of the
 * interface language, so case is folded the same way in every language.
 */
export function sameNameExists(raw: string, names: readonly string[]): boolean {
  const name = normalizeText(raw).toLocaleLowerCase('en-US');
  return name !== '' && names.some((other) => other.normalize('NFC').toLocaleLowerCase('en-US') === name);
}
