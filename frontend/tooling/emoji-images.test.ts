import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import fixture from '../../docs/contracts/test-data/picker-emoji.json';
import { emojiImageSet } from '../apps/web/emoji-image-set.ts';
import { loadEmojiImages } from '../apps/web/emoji-images-plugin.ts';
import { flagsChecksum, shrinkFlag } from './emoji-flags.js';

const vendored = new URL('../apps/web/emoji-images/noto/', import.meta.url);
const source = JSON.parse(readFileSync(new URL('SOURCE.json', vendored), 'utf8')) as {
  commit: string;
  flagsSha256: string;
};

/** The image key as src/features/emoji/images.ts builds it. */
const keyOf = (emoji: string) =>
  Array.from(emoji, (character) => (character.codePointAt(0) ?? 0).toString(16))
    .filter((codePoint) => codePoint !== 'fe0f')
    .join('-');

const images = emojiImageSet.images();

describe('Emoji image set', () => {
  it('EveryEmojiThePickerOffers_HasAnImage_FlagsAndSkinTonesIncluded', () => {
    const offered = Object.values(fixture.offered).flatMap((group) => group.split(' '));

    expect(offered.length).toBeGreaterThan(3400);
    expect(offered.filter((emoji) => !images.has(keyOf(emoji)))).toEqual([]);
  });

  it('Keys_AreLowerCaseHexCodePointsWithoutTheEmojiSelector', () => {
    for (const key of images.keys()) {
      expect(key).toMatch(/^[1-9a-f][0-9a-f]*(-[1-9a-f][0-9a-f]*)*$/);
      expect(key.split('-')).not.toContain('fe0f');
    }
    for (const key of ['1f600', '2764', '31-20e3', '1f1f9-1f1f7', '1f3f3-200d-1f308']) {
      expect(images.has(key), key).toBe(true);
    }
    // A region without a flag of its own shows the flag upstream gives it.
    expect(images.get('1f1fa-1f1f2')).toBe(images.get('1f1fa-1f1f8'));
  });

  it('SameImages_AreShippedOnce_AndTheOtherEmojiPointAtThem', () => {
    // The set read once above: reading and parsing every image again took seconds and timed out under a full run.
    const shipped = loadEmojiImages({ ...emojiImageSet, images: () => images });
    const aliases = Object.entries(shipped.aliases);

    // The eight regions that show another region's flag, and emoji upstream draws alike.
    expect(aliases.length).toBeGreaterThanOrEqual(8);
    expect(shipped.images.size + aliases.length).toBe(images.size);
    expect(new Set(shipped.images.values()).size).toBe(shipped.images.size);
    for (const [key, target] of aliases) {
      expect(shipped.images.has(key), key).toBe(false);
      expect(shipped.images.get(target), key).toBe(images.get(key));
    }
    // Of two regions with one flag, the first in key order has the file.
    expect(shipped.aliases['1f1fa-1f1f8']).toBe('1f1fa-1f1f2');
    expect(shipped.folder).toMatch(/^assets\/emoji-[0-9a-f]{12}\/$/);
  });

  it('Images_AreSvgThatLoadsNothingAndRunsNothing', () => {
    for (const [key, image] of images) {
      expect(image.startsWith('<svg xmlns="http://www.w3.org/2000/svg"'), key).toBe(true);
      expect(
        /<script|<foreignObject|<image|@import|\son[a-z]+=|href="(?!#)|url\((?!#)/i.test(image),
        key,
      ).toBe(false);
    }
  });

  it('VendoredFlags_AreTheOnesThatWereFetchedFromThePinnedCommit', () => {
    expect(source.commit).toMatch(/^[0-9a-f]{40}$/);
    // On a difference: someone edited the files by hand; run `pnpm -C frontend update:emoji-flags`.
    expect(flagsChecksum(fileURLToPath(new URL('flags', vendored)))).toBe(source.flagsSha256);
  });

  it('ShrinkFlag_RoundsPathCoordinates_AndRefusesWhatRoundingWouldBreak', () => {
    const flag =
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1000">\n  <defs/>\n' +
      '  <path fill="#E30A17" opacity="0.5" d="M8,195.25 C178.83,-110.5 349.03,140 .6,1"/>\n</svg>\n';

    expect(shrinkFlag(flag)).toBe(
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1000">' +
        '<path fill="#E30A17" opacity="0.5" d="M8,195 C179,-110 349,140 1,1"/></svg>\n',
    );
    expect(() => shrinkFlag(flag.replace('1000 1000', '36 36'))).toThrow();
    expect(() => shrinkFlag(flag.replace('C178', 'c178'))).toThrow();
  });

  it('Notices_CarryTheLicensesOfTheImagesAndOfTheFlags', () => {
    const [emoji, flags] = emojiImageSet.notices();

    expect(emoji).toContain('@iconify-json/noto');
    expect(emoji).toContain('Licensed under the Apache License, Version 2.0');
    expect(emoji).toContain('TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION');
    expect(flags).toContain(source.commit);
    expect(flags).toContain('Public Domain or otherwise exempt from Copyright');
    expect(flags).toContain('rounded');
  });
});
