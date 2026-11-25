// Serves the emoji images (emoji-image-set.ts) from the app itself, so no emoji is fetched from another host:
//
// - a build writes every image to `assets/emoji-<hash>/<image key>.svg`. The hash is of the whole set, so the files
//   can be cached as immutable like every other hashed asset, and an address follows from the emoji alone: the app
//   ships no list of images, and none of them is part of a JavaScript or CSS file. Emoji that share an image (a
//   region that shows another region's flag) get one file; the app knows these few by name;
// - the dev server answers the same addresses from memory;
// - `virtual:emoji-images` gives the app the folder's address and the emoji that share another one's image.
import { createHash } from 'node:crypto';
import type { Plugin } from 'vite';
import type { EmojiImageSet } from './emoji-image-set.ts';

const MODULE = 'virtual:emoji-images';

interface Loaded {
  folder: string;
  /** One entry per different image. */
  images: Map<string, string>;
  /** The keys left out of `images`, with the key that has the same image. */
  aliases: Record<string, string>;
}

/** The set with each image once: of keys with the same image, the first in key order keeps it. */
export function loadEmojiImages(set: EmojiImageSet): Loaded {
  const images = new Map<string, string>();
  const aliases: Record<string, string> = {};
  const keyOfImage = new Map<string, string>();
  const hash = createHash('sha256');
  for (const [key, image] of [...set.images()].sort(([a], [b]) => (a < b ? -1 : 1))) {
    hash.update(`${key}\n${image}\n`);
    const same = keyOfImage.get(image);
    if (same === undefined) {
      keyOfImage.set(image, key);
      images.set(key, image);
    } else {
      aliases[key] = same;
    }
  }
  return { folder: `assets/emoji-${hash.digest('hex').slice(0, 12)}/`, images, aliases };
}

export function emojiImages(set: EmojiImageSet): Plugin {
  let base = '/';
  let testing = false;
  let loaded: Loaded | undefined;
  const load = () => (loaded ??= loadEmojiImages(set));

  return {
    name: 'backroom:emoji-images',
    configResolved(config) {
      base = config.base;
      testing = config.mode === 'test';
    },
    resolveId(id) {
      return id === MODULE ? `\0${MODULE}` : undefined;
    },
    load(id) {
      if (id !== `\0${MODULE}`) return undefined;
      // Tests draw no images, so they do not read the set.
      const { folder, aliases } = testing ? { folder: 'assets/emoji-test/', aliases: {} } : load();
      return [
        `export const emojiImageBase = ${JSON.stringify(base + folder)};`,
        `export const emojiImageAliases = ${JSON.stringify(aliases)};`,
      ].join('\n');
    },
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const path = (request.url ?? '').split('?')[0] ?? '';
        if (!path.endsWith('.svg') || !path.includes('/emoji-')) {
          next();
          return;
        }
        const { folder, images } = load();
        const prefix = base + folder;
        const image = path.startsWith(prefix)
          ? images.get(path.slice(prefix.length, -'.svg'.length))
          : undefined;
        if (image === undefined) {
          next();
          return;
        }
        response.setHeader('Content-Type', 'image/svg+xml');
        response.setHeader('Cache-Control', 'public, max-age=31536000, immutable');
        response.end(image);
      });
    },
    generateBundle() {
      const { folder, images } = load();
      for (const [key, image] of images) {
        this.emitFile({ type: 'asset', fileName: `${folder}${key}.svg`, source: image });
      }
    },
  };
}
