// Public surface of the emoji feature: the shared picker (custom status, reactions, the composer) and the emoji
// names. Emoji are always picked from this list, never typed.
export { EmojiPicker, type EmojiPickerProps } from './emoji-picker';
export { loadEmojiData } from './load';
export type { Emoji, EmojiData, EmojiGroup, EmojiGroupKey } from './data';
