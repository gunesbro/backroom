// Serves the emoji images (emoji-image-set.ts) from the app itself, so no emoji is fetched from another host:
//
// - a build writes every image to `assets/emoji-<hash>/<image key>.svg`. The hash is of the whole set, so the files
//   can be cached as immutable like every other hashed asset, and an address follows from the emoji alone: the app
//   ships no list of images, and none of them is part of a JavaScript or CSS file;
// - the dev server answers the same addresses from memory;
// - `virtual:emoji-images` gives the app the folder's address.
import { createHash } from 'node:crypto';
import type { Plugin } from 'vite';
import type { EmojiImageSet } from './emoji-image-set.ts';

const MODULE = 'virtual:emoji-images';

export function emojiImages(set: EmojiImageSet): Plugin {
  let base = '/';
  let testing = false;
  let loaded: { folder: string; images: Map<string, string> } | undefined;
  const load = () => {
    if (!loaded) {
      const images = set.images();
      const hash = createHash('sha256');
      for (const key of [...images.keys()].sort()) hash.update(`${key}\n${images.get(key) ?? ''}\n`);
      loaded = { folder: `assets/emoji-${hash.digest('hex').slice(0, 12)}/`, images };
    }
    return loaded;
  };

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
      const folder = testing ? 'assets/emoji-test/' : load().folder;
      return `export const emojiImageBase = ${JSON.stringify(base + folder)};`;
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
