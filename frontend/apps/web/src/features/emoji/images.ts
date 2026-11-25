// Where the image of an emoji is. The images are files of the app itself (emoji-images-plugin.ts); which image set
// they come from is decided at build time (emoji-image-set.ts) and is not known here.
import { emojiImageBase } from 'virtual:emoji-images';

/**
 * The name of an emoji's image: its code points in lower-case hex joined by "-", without the emoji presentation
 * selector, so ❤ U+FE0F and ❤ name the same image, and so do both spellings of a joined sequence.
 */
export function emojiImageKey(emoji: string): string {
  const codePoints: string[] = [];
  for (const character of emoji) {
    const codePoint = character.codePointAt(0) ?? 0;
    if (codePoint !== 0xfe0f) codePoints.push(codePoint.toString(16));
  }
  return codePoints.join('-');
}

export function emojiImageUrl(emoji: string): string {
  return `${emojiImageBase}${emojiImageKey(emoji)}.svg`;
}
