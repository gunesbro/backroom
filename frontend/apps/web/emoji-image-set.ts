// The emoji image set: where the pictures come from, how the source names its files and what its licenses ask us
// to ship. This is the only file that knows the set; to change to another one, write another `EmojiImageSet` and
// export it as `emojiImageSet`. Everything else (the Vite plugin, rendering, the composer, the picker) works with
// image keys only.
//
// An image key is the emoji's code points in lower-case hex joined by "-", without the emoji presentation selector
// (U+FE0F): "1f600", "1f44d-1f3fd", "1f1f9-1f1f7", "31-20e3". src/features/emoji/images.ts builds the same key from
// a string.
//
// The set in use is Noto Emoji (https://github.com/googlefonts/noto-emoji):
// - the emoji come from the npm package @iconify-json/noto (Apache-2.0), which carries upstream's SVG files as data;
// - upstream keeps country and region flags apart and the package has none of them, so they are vendored under
//   emoji-images/noto/flags by tooling/update-emoji-flags.js from the commit pinned in emoji-images/noto/SOURCE.json.
import { readFileSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { roundSvgPaths } from './svg-path.ts';

export interface EmojiImageSet {
  /** Every image as SVG text, by image key. Emoji that look the same may share a text: it is shipped once. */
  images: () => Map<string, string>;
  /** What the licenses ask us to ship with the images: one section of the third-party notices each. */
  notices: () => string[];
}

/** The image key of code points written in hex, whatever their case, padding or selectors. */
function keyOf(codePoints: readonly string[]): string {
  return codePoints
    .map((codePoint) => Number.parseInt(codePoint, 16).toString(16))
    .filter((codePoint) => codePoint !== 'fe0f')
    .join('-');
}

const require = createRequire(import.meta.url);
const PACKAGE = '@iconify-json/noto';
const VENDORED = new URL('./emoji-images/noto/', import.meta.url);

const readPackage = (file: string): unknown =>
  JSON.parse(readFileSync(require.resolve(`${PACKAGE}/${file}`), 'utf8'));
const readVendored = (file: string): string => readFileSync(fileURLToPath(new URL(file, VENDORED)), 'utf8');

interface IconSet {
  icons: Record<string, { body: string } | undefined>;
  aliases?: Record<string, { parent: string } | undefined>;
  width: number;
  height: number;
}

interface FlagSource {
  repository: string;
  tag: string;
  commit: string;
  aliases: Record<string, string>;
}

const flagSource = (): FlagSource => JSON.parse(readVendored('SOURCE.json')) as FlagSource;

/** Upstream's name of an image, "emoji_u1f1f9_1f1f7.svg", as an image key. */
const flagKey = (file: string): string => keyOf(file.replace(/^emoji_u|\.svg$/g, '').split('_'));

function images(): Map<string, string> {
  const result = new Map<string, string>();
  const set = readPackage('icons.json') as IconSet;
  const size = `${String(set.width)} ${String(set.height)}`;
  // The package's list of emoji: code points ("1f44d-1f3fd", "0031-20e3") to the name of an icon.
  const chars = readPackage('chars.json') as Record<string, string>;
  for (const [codePoints, name] of Object.entries(chars)) {
    const icon = set.icons[name] ?? set.icons[set.aliases?.[name]?.parent ?? ''];
    if (!icon) continue;
    result.set(
      keyOf(codePoints.split('-')),
      // One decimal on upstream's 128 unit canvas is far below a pixel at any size an emoji is shown.
      `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size}">${roundSvgPaths(icon.body, 1)}</svg>`,
    );
  }
  const flags = new URL('flags/', VENDORED);
  for (const file of readdirSync(fileURLToPath(flags))) {
    if (file.endsWith('.svg')) result.set(flagKey(file), readVendored(`flags/${file}`).trim());
  }
  // A region without a flag of its own shows another region's.
  for (const [file, target] of Object.entries(flagSource().aliases)) {
    const image = result.get(flagKey(target));
    if (image) result.set(flagKey(file), image);
  }
  return result;
}

function notices(): string[] {
  const { version } = readPackage('package.json') as { version: string };
  const source = flagSource();
  return [
    [
      `Emoji images: Noto Emoji, ${source.repository}, as packaged in ${PACKAGE} ${version}`,
      '',
      readVendored('LICENSE').trim(),
      '',
      readVendored('APACHE-2.0.txt').trim(),
    ].join('\n'),
    [
      `Flag images: Noto Emoji, ${source.repository}, third_party/region-flags/waved-svg at ${source.tag}`,
      `(commit ${source.commit}). Changed: path coordinates are rounded to whole numbers.`,
      '',
      readVendored('flags/LICENSE').trim(),
    ].join('\n'),
  ];
}

export const emojiImageSet: EmojiImageSet = { images, notices };
