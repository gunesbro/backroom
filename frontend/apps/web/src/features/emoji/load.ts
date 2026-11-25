import type { Locale } from '@backroom/i18n';
import type { EmojiData } from './data';

/**
 * The emoji with names in `locale`, for the picker and for naming an emoji elsewhere. The data is its own chunk,
 * fetched on first use and kept.
 */
export function loadEmojiData(locale: Locale): Promise<EmojiData> {
  return import('./data').then((module) => module.loadEmojiData(locale));
}
