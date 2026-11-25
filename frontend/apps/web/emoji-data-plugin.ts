// Emoji data for the picker (src/features/emoji), built from npm packages when the app is built, so the data is
// part of our own bundle and nothing is fetched from another host at runtime:
//
// - `virtual:emoji-data/en`: the emoji in picker order with English names and keywords (emojibase-data, MIT);
// - `virtual:emoji-data/tr`: Turkish names and keywords for the same emoji (CLDR annotations, Unicode-3.0).
//
// Only what the picker needs is kept, which makes each module a fraction of the source files.
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import type { Plugin } from 'vite';

/**
 * The newest emoji version offered. Newer emoji are still missing from the system fonts of many machines and would
 * show as empty boxes; raise this when the platforms we support have caught up.
 */
const MAX_EMOJI_VERSION = 15.1;

/**
 * Text symbols that only look like emoji with a variation selector (©, ®, ™). The server does not take them as an
 * emoji (AccountStatusRules.IsEmojiBase), so they are not offered.
 */
const NOT_OFFERED: ReadonlySet<string> = new Set(['\u00A9\uFE0F', '\u00AE\uFE0F', '\u2122\uFE0F']);

// emojibase's group numbers; 2 is "component" (skin tones, hair), which is not picked on its own.
const GROUPS: Readonly<Record<number, string>> = {
  0: 'smileys-emotion',
  1: 'people-body',
  3: 'animals-nature',
  4: 'food-drink',
  5: 'travel-places',
  6: 'activities',
  7: 'objects',
  8: 'symbols',
  9: 'flags',
};

interface SourceEmoji {
  label: string;
  emoji: string;
  tags?: string[];
  order?: number;
  group?: number;
  version: number;
  tone?: number | number[];
  skins?: SourceEmoji[];
}

interface Annotation {
  default?: string[];
  tts?: string[];
}

/** `[emoji, name, keywords, the five skin tones from light to dark when it has them]` */
type Entry = [string, string, string] | [string, string, string, string[]];

const require = createRequire(import.meta.url);
const readJson = (id: string): unknown => JSON.parse(readFileSync(require.resolve(id), 'utf8'));

function sourceEmojis(): SourceEmoji[] {
  return (readJson('emojibase-data/en/data.json') as SourceEmoji[])
    .filter(
      (emoji) =>
        emoji.group !== undefined &&
        Object.hasOwn(GROUPS, emoji.group) &&
        emoji.version <= MAX_EMOJI_VERSION &&
        !NOT_OFFERED.has(emoji.emoji),
    )
    .sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
}

/** A reaction is at most this many bytes of UTF-8 (docs/contracts/phase-2.md), and the picker serves reactions too. */
const MAX_EMOJI_BYTES = 32;

/**
 * The variants with one skin tone for everyone in the picture, light to dark; none unless all five exist and fit
 * (a kiss between two named people with skin tones is 35 bytes, so those three keep only their default form).
 */
function skinTones(emoji: SourceEmoji): string[] | undefined {
  const tones = (emoji.skins ?? [])
    .filter((skin) => typeof skin.tone === 'number' && skin.version <= MAX_EMOJI_VERSION)
    .sort((a, b) => (a.tone as number) - (b.tone as number))
    .map((skin) => skin.emoji);
  const fits = tones.every((tone) => Buffer.byteLength(tone, 'utf8') <= MAX_EMOJI_BYTES);
  return tones.length === 5 && fits ? tones : undefined;
}

/**
 * Every emoji the picker offers, skin tones included, by group in picker order. The server must accept each of them:
 * docs/contracts/test-data/picker-emoji.json is this list, checked on both sides.
 */
export function offeredEmojis(): Record<string, string[]> {
  return Object.fromEntries(
    english().map(([group, entries]) => [
      group,
      entries.flatMap(([emoji, , , tones]) => [emoji, ...(tones ?? [])]),
    ]),
  );
}

function english(): [string, Entry[]][] {
  const groups = new Map<string, Entry[]>(Object.values(GROUPS).map((key) => [key, []]));
  for (const emoji of sourceEmojis()) {
    const tones = skinTones(emoji);
    const tags = (emoji.tags ?? []).join(' ');
    groups
      .get(GROUPS[emoji.group ?? -1] ?? '')
      ?.push(tones ? [emoji.emoji, emoji.label, tags, tones] : [emoji.emoji, emoji.label, tags]);
  }
  return [...groups];
}

/** The region of a country flag (two regional indicator letters), or undefined for any other emoji. */
function flagRegion(emoji: string): string | undefined {
  const letters = Array.from(emoji, (character) => (character.codePointAt(0) ?? 0) - 0x1f1e6);
  if (letters.length !== 2 || letters.some((letter) => letter < 0 || letter > 25)) return undefined;
  return String.fromCharCode(...letters.map((letter) => letter + 65));
}

function turkish(): Record<string, [string, string]> {
  const source = readJson('cldr-annotations-full/annotations/tr/annotations.json') as {
    annotations: { annotations: Record<string, Annotation> };
  };
  const annotations = source.annotations.annotations;
  const regions = new Intl.DisplayNames('tr', { type: 'region', fallback: 'none' });
  const names: Record<string, [string, string]> = {};
  for (const { emoji } of sourceEmojis()) {
    // CLDR lists the characters without the emoji presentation selector.
    const annotation = annotations[emoji.replaceAll('\uFE0F', '')];
    const name = annotation?.tts?.[0];
    if (name) {
      names[emoji] = [name, (annotation.default ?? []).join(' ')];
      continue;
    }
    // Country flags are named after their region in CLDR ("bayrak: Türkiye").
    const region = flagRegion(emoji);
    const regionName = region && regions.of(region);
    if (regionName) names[emoji] = [`bayrak: ${regionName}`, 'bayrak'];
  }
  return names;
}

const NOTICES_FILE = 'third-party-notices.txt';

interface Source {
  title: string;
  packages: readonly string[];
  /** One line for the banner of the module built from it. */
  credit: string;
  build: () => unknown;
}

const SOURCES: Readonly<Record<string, Source>> = {
  en: {
    title: 'Emoji names and keywords in English',
    packages: ['emojibase-data'],
    credit: 'emojibase-data, Copyright (c) 2017-2019 Miles Johnson, MIT License',
    build: english,
  },
  tr: {
    title: 'Emoji names and keywords in Turkish',
    packages: ['cldr-annotations-full'],
    credit: 'Unicode CLDR annotations, Copyright (c) Unicode, Inc., Unicode License v3',
    build: turkish,
  },
};

/** The copyright and permission notices of the data packages, as their licenses ask of whoever ships the data. */
function notices(): string {
  const parts = Object.values(SOURCES).flatMap((source) =>
    source.packages.map((name) => {
      const manifest = require.resolve(`${name}/package.json`);
      const { version } = JSON.parse(readFileSync(manifest, 'utf8')) as { version: string };
      const license = readFileSync(join(dirname(manifest), 'LICENSE'), 'utf8').trim();
      return [`${source.title}: ${name} ${version}`, '', license].join('\n');
    }),
  );
  const rule = ['', '', '-'.repeat(78), '', ''].join('\n');
  return ['This application includes data from the following projects.', ...parts].join(rule) + '\n';
}

const PREFIX = 'virtual:emoji-data/';

export function emojiData(): Plugin {
  return {
    name: 'backroom:emoji-data',
    resolveId(id) {
      return id.startsWith(PREFIX) ? `\0${id}` : undefined;
    },
    load(id) {
      if (!id.startsWith(`\0${PREFIX}`)) return undefined;
      const key = id.slice(PREFIX.length + 1);
      const source = Object.hasOwn(SOURCES, key) ? SOURCES[key] : undefined;
      if (!source) throw new Error(`Unknown emoji data module: ${id.slice(1)}`);
      // A JSON string parses faster than the same data as a JavaScript literal.
      return `export default JSON.parse(${JSON.stringify(JSON.stringify(source.build()))});`;
    },
    generateBundle(_options, bundle) {
      // Each data chunk starts with a legal comment naming its source. It is added to the finished chunk, because
      // a comment inside the module does not survive bundling.
      for (const chunk of Object.values(bundle)) {
        if (chunk.type !== 'chunk') continue;
        const credits = Object.entries(SOURCES)
          .filter(([key]) => chunk.moduleIds.includes(`\0${PREFIX}${key}`))
          .map(([, source]) => source.credit);
        if (credits.length > 0) {
          chunk.code = `/*! ${credits.join('; ')}. Full notice: /${NOTICES_FILE} */\n${chunk.code}`;
        }
      }
      // The full notices ship with the web app and, through it, with the desktop app.
      this.emitFile({ type: 'asset', fileName: NOTICES_FILE, source: notices() });
    },
  };
}
