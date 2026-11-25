import type { Locale } from '@backroom/i18n';

/** The groups of the picker, in order (Unicode's emoji groups). */
export const GROUP_KEYS = [
  'smileys-emotion',
  'people-body',
  'animals-nature',
  'food-drink',
  'travel-places',
  'activities',
  'objects',
  'symbols',
  'flags',
] as const;

export type EmojiGroupKey = (typeof GROUP_KEYS)[number];

export interface Emoji {
  readonly emoji: string;
  /** The name in the person's language (English where there is no translation). */
  readonly label: string;
  /** Names and keywords, folded for searching. */
  readonly search: string;
  /** The same emoji in the five skin tones, light to dark. */
  readonly skins?: readonly string[];
}

export interface EmojiGroup {
  readonly key: EmojiGroupKey;
  readonly emojis: readonly Emoji[];
}

export interface EmojiData {
  readonly groups: readonly EmojiGroup[];
  /** Every emoji and skin tone variant, for looking up a stored or received emoji. */
  readonly byEmoji: ReadonlyMap<string, Emoji>;
}

/**
 * Text as it is compared in a search: lower case without accents, and the Turkish dotless ı as i, so "gulen" finds
 * "gülen" and "ISIK" finds "ışık" whatever the machine's language is.
 */
export function fold(text: string): string {
  return text.toLowerCase().normalize('NFD').replaceAll(/\p{M}/gu, '').replaceAll('ı', 'i');
}

/** The most results a search shows. */
export const SEARCH_LIMIT = 96;

/** Emoji whose name or keywords contain the query: names that start with it first, then words that do, then the rest. */
export function searchEmojis(
  groups: readonly EmojiGroup[],
  query: string,
  limit: number = SEARCH_LIMIT,
): Emoji[] {
  const wanted = fold(query.trim());
  if (wanted === '') return [];
  const starts: Emoji[] = [];
  const words: Emoji[] = [];
  const rest: Emoji[] = [];
  for (const group of groups) {
    for (const emoji of group.emojis) {
      const at = emoji.search.indexOf(wanted);
      if (at === 0) starts.push(emoji);
      else if (at > 0) (emoji.search[at - 1] === ' ' ? words : rest).push(emoji);
    }
  }
  return [...starts, ...words, ...rest].slice(0, limit);
}

const isGroupKey = (key: string): key is EmojiGroupKey => (GROUP_KEYS as readonly string[]).includes(key);

async function build(locale: Locale): Promise<EmojiData> {
  // English is always loaded: it names what has no translation, and people search in it in every language.
  const [english, translated] = await Promise.all([
    import('virtual:emoji-data/en'),
    locale === 'tr' ? import('virtual:emoji-data/tr') : undefined,
  ]);
  const names = translated?.default ?? {};
  const byEmoji = new Map<string, Emoji>();
  const groups: EmojiGroup[] = [];
  for (const [key, entries] of english.default) {
    if (!isGroupKey(key)) continue;
    const emojis = entries.map(([emoji, name, keywords, skins]): Emoji => {
      const local = names[emoji];
      const entry: Emoji = {
        emoji,
        label: local?.[0] ?? name,
        search: fold(local ? `${local[0]} ${local[1]} ${name} ${keywords}` : `${name} ${keywords}`),
        ...(skins ? { skins } : {}),
      };
      byEmoji.set(emoji, entry);
      for (const skin of skins ?? []) byEmoji.set(skin, entry);
      return entry;
    });
    groups.push({ key, emojis });
  }
  return { groups, byEmoji };
}

// One entry per language (two at most).
const loaded = new Map<Locale, Promise<EmojiData>>();

/** The emoji with names in `locale`; fetched once per language and kept. A failed load is tried again next time. */
export function loadEmojiData(locale: Locale): Promise<EmojiData> {
  let data = loaded.get(locale);
  if (!data) {
    data = build(locale);
    loaded.set(locale, data);
    data.catch(() => {
      loaded.delete(locale);
    });
  }
  return data;
}
