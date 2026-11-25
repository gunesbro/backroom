// The module built by emoji-images-plugin.ts.

declare module 'virtual:emoji-images' {
  /** The address of the folder with the emoji images, ending in "/"; an image is `<image key>.svg` in it. */
  export const emojiImageBase: string;
  /** Image keys that have no file of their own, with the key of the image they show (the same picture). */
  export const emojiImageAliases: Readonly<Record<string, string | undefined>>;
}
