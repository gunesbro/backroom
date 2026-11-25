// Public surface of the emoji feature: the shared picker (custom status, reactions, the composer), the emoji
// names, and emoji drawn as images of the app's own set. Emoji are always picked from this list, never typed.
export { EmojiPicker, type EmojiPickerProps } from './emoji-picker';
export { EmojiImage, EmojiText } from './emoji-image';
export { emojiImageUrl } from './images';
export { firstEmoji, isEmojiOnly, type EmojiMatch } from './segment';
export { installEmojiCopy } from './copy';
export { loadEmojiData } from './load';
export type { Emoji, EmojiData, EmojiGroup, EmojiGroupKey } from './data';
