// The modules built by emoji-data-plugin.ts (see there for where the data comes from).

declare module 'virtual:emoji-data/en' {
  /** Groups in picker order; each emoji is `[emoji, name, keywords, skin tones from light to dark]`. */
  const groups: readonly (readonly [
    group: string,
    emojis: readonly (readonly [emoji: string, name: string, keywords: string, skins?: readonly string[]])[],
  ])[];
  export default groups;
}

declare module 'virtual:emoji-data/tr' {
  /** Turkish `[name, keywords]` by emoji; an emoji CLDR has no Turkish name for is missing. */
  const names: Readonly<Record<string, readonly [name: string, keywords: string]>>;
  export default names;
}
