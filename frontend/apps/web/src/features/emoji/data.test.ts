import { describe, expect, it } from 'vitest';
import { fold, GROUP_KEYS, loadEmojiData, searchEmojis } from './data';

const segmenter = new Intl.Segmenter('en-US', { granularity: 'grapheme' });
const codePoints = (value: string) => Array.from(value, (character) => character.codePointAt(0) ?? 0);
const inRange = (value: number, from: number, to: number) => value >= from && value <= to;

// The emoji of Miscellaneous Symbols and Dingbats the server takes (AccountStatusRules.IsSymbolEmoji).
const SYMBOL_EMOJI = new Set(
  [
    [0x2600, 0x2604],
    0x260e,
    0x2611,
    0x2614,
    0x2615,
    0x2618,
    0x261d,
    0x2620,
    0x2622,
    0x2623,
    0x2626,
    0x262a,
    0x262e,
    0x262f,
    [0x2638, 0x263a],
    0x2640,
    0x2642,
    [0x2648, 0x2653],
    0x265f,
    0x2660,
    0x2663,
    0x2665,
    0x2666,
    0x2668,
    0x267b,
    0x267e,
    0x267f,
    [0x2692, 0x2697],
    0x2699,
    0x269b,
    0x269c,
    0x26a0,
    0x26a1,
    0x26a7,
    0x26aa,
    0x26ab,
    0x26b0,
    0x26b1,
    0x26bd,
    0x26be,
    0x26c4,
    0x26c5,
    0x26c8,
    0x26ce,
    0x26cf,
    0x26d1,
    0x26d3,
    0x26d4,
    0x26e9,
    0x26ea,
    [0x26f0, 0x26f5],
    [0x26f7, 0x26fa],
    0x26fd,
    0x2702,
    0x2705,
    [0x2708, 0x270d],
    0x270f,
    0x2712,
    0x2714,
    0x2716,
    0x271d,
    0x2721,
    0x2728,
    0x2733,
    0x2734,
    0x2744,
    0x2747,
    0x274c,
    0x274e,
    [0x2753, 0x2755],
    0x2757,
    0x2763,
    0x2764,
    [0x2795, 0x2797],
    0x27a1,
    0x27b0,
    0x27bf,
  ].flatMap((item) =>
    typeof item === 'number'
      ? [item]
      : Array.from({ length: (item[1] ?? 0) - (item[0] ?? 0) + 1 }, (_, at) => (item[0] ?? 0) + at),
  ),
);

/** AccountStatusRules.IsEmojiBase on the server, kept in step by hand: the code points that are emoji on their own. */
function isEmojiBase(value: number): boolean {
  if (inRange(value, 0x1f000, 0x1f02f)) return value === 0x1f004;
  if (inRange(value, 0x1f0a0, 0x1f0ff)) return value === 0x1f0cf;
  if (inRange(value, 0x1f170, 0x1f1ff)) {
    return [0x1f170, 0x1f171, 0x1f17e, 0x1f17f, 0x1f18e].includes(value) || inRange(value, 0x1f191, 0x1f19a);
  }
  if (inRange(value, 0x1f200, 0x1f2ff)) {
    return (
      [0x1f201, 0x1f202, 0x1f21a, 0x1f22f, 0x1f250, 0x1f251].includes(value) ||
      inRange(value, 0x1f232, 0x1f23a)
    );
  }
  if (inRange(value, 0x1f3fb, 0x1f3ff)) return false;
  if (
    inRange(value, 0x1f300, 0x1f64f) ||
    inRange(value, 0x1f680, 0x1f6ff) ||
    inRange(value, 0x1f7e0, 0x1f7eb) ||
    value === 0x1f7f0 ||
    inRange(value, 0x1f90c, 0x1f9ff) ||
    inRange(value, 0x1fa70, 0x1faff)
  ) {
    return true;
  }
  if ([0x203c, 0x2049, 0x2139, 0x21a9, 0x21aa].includes(value) || inRange(value, 0x2194, 0x2199)) return true;
  if (
    [0x231a, 0x231b, 0x2328, 0x23cf, 0x24c2].includes(value) ||
    inRange(value, 0x23e9, 0x23f3) ||
    inRange(value, 0x23f8, 0x23fa)
  ) {
    return true;
  }
  if ([0x25aa, 0x25ab, 0x25b6, 0x25c0].includes(value) || inRange(value, 0x25fb, 0x25fe)) return true;
  if (inRange(value, 0x2600, 0x27bf)) return SYMBOL_EMOJI.has(value);
  if ([0x2934, 0x2935, 0x2b1b, 0x2b1c, 0x2b50, 0x2b55].includes(value) || inRange(value, 0x2b05, 0x2b07)) {
    return true;
  }
  return [0x3030, 0x303d, 0x3297, 0x3299].includes(value);
}

/** AccountStatusRules.TryReadEmoji on the server: what `PUT /api/v1/me/status` accepts as the one emoji. */
function serverAccepts(emoji: string): boolean {
  if (emoji.length > 32 || emoji.normalize('NFC') !== emoji) return false;
  if (Array.from(segmenter.segment(emoji)).length !== 1) return false;
  const runes = codePoints(emoji);
  const [first = 0, second = 0] = runes;
  if (runes.length > 16) return false;
  const isRegional = (value: number) => inRange(value, 0x1f1e6, 0x1f1ff);
  if (isRegional(first)) return runes.length === 2 && isRegional(second);
  if (inRange(first, 0x30, 0x39) || first === 0x23 || first === 0x2a) {
    return (
      (runes.length === 3 && second === 0xfe0f && runes[2] === 0x20e3) ||
      (runes.length === 2 && second === 0x20e3)
    );
  }
  if (first === 0x1f3f4 && runes.length > 2 && inRange(second, 0xe0020, 0xe007e)) {
    return runes.at(-1) === 0xe007f && runes.slice(1, -1).every((rune) => inRange(rune, 0xe0020, 0xe007e));
  }
  let expectBase = true;
  for (const rune of runes) {
    if (expectBase) {
      if (!isEmojiBase(rune)) return false;
      expectBase = false;
    } else if (rune === 0x200d) {
      expectBase = true;
    } else if (!inRange(rune, 0x1f3fb, 0x1f3ff) && rune !== 0xfe0f) {
      return false;
    }
  }
  return !expectBase;
}

describe('Emoji data', () => {
  it('Groups_AreTheNineOfThePickerInOrder_WithoutSkinToneOrHairComponents', async () => {
    const data = await loadEmojiData('en');

    expect(data.groups.map((group) => group.key)).toEqual(GROUP_KEYS);
    for (const group of data.groups) expect(group.emojis.length).toBeGreaterThan(50);
    expect(data.byEmoji.has('🏻')).toBe(false);
    expect(data.byEmoji.has('🦰')).toBe(false);
  });

  it('EveryEmojiAndSkinTone_IsOneThatTheServerAcceptsAsAStatusOrAReaction', async () => {
    const data = await loadEmojiData('en');
    const all = [...data.byEmoji.keys()];

    // About 1,900 emoji and five skin tones for some 330 of them.
    expect(all.length).toBeGreaterThan(3400);
    expect(all.filter((emoji) => !serverAccepts(emoji))).toEqual([]);
    // A reaction is at most 32 bytes of UTF-8 (docs/contracts/phase-2.md).
    const bytes = new TextEncoder();
    expect(all.filter((emoji) => bytes.encode(emoji).length > 32)).toEqual([]);
  });

  it('Emoji_NewerThanTheSupportedVersion_AreLeftOut', async () => {
    const data = await loadEmojiData('en');

    // Emoji 15.1 is in, Emoji 16 and later (which many machines cannot draw yet) are not.
    expect(data.byEmoji.get('🐦‍🔥')?.label).toBe('phoenix');
    expect(data.byEmoji.has('\u{1FAE9}')).toBe(false);
    expect(searchEmojis(data.groups, 'fingerprint')).toEqual([]);
  });

  it('SkinTones_AreFiveFromLightToDark_AndLeadBackToTheirEmoji', async () => {
    const data = await loadEmojiData('en');
    const wave = data.byEmoji.get('👋');

    expect(wave?.skins).toEqual(['👋🏻', '👋🏼', '👋🏽', '👋🏾', '👋🏿']);
    expect(data.byEmoji.get('👋🏽')).toBe(wave);
    expect(data.byEmoji.get('🚀')?.skins).toBeUndefined();
  });

  it('TurkishNames_CoverMostEmoji_AndFallBackToEnglish', async () => {
    const [english, turkish] = await Promise.all([loadEmojiData('en'), loadEmojiData('tr')]);
    const emojis = turkish.groups.flatMap((group) => group.emojis);
    const translated = emojis.filter((emoji) => emoji.label !== english.byEmoji.get(emoji.emoji)?.label);

    expect(emojis).toHaveLength(english.groups.flatMap((group) => group.emojis).length);
    expect(translated.length / emojis.length).toBeGreaterThan(0.9);
    expect(turkish.byEmoji.get('😀')?.label).toBe('sırıtan yüz');
    expect(turkish.byEmoji.get('🇯🇵')?.label).toBe('bayrak: Japonya');
    // Not named in Turkish by CLDR's base list: the English name is used and found.
    expect(turkish.byEmoji.get('👨‍🦰')?.label).toBe(english.byEmoji.get('👨‍🦰')?.label);
    expect(emojis.every((emoji) => emoji.label !== '' && emoji.search !== '')).toBe(true);
  });

  it('LoadEmojiData_CalledAgain_ReturnsTheSameData', async () => {
    expect(await loadEmojiData('tr')).toBe(await loadEmojiData('tr'));
  });
});

describe('fold', () => {
  it('Text_InAnyCase_ComparesWithoutAccentsOrTheTurkishDotDifference', () => {
    expect(fold('GÜLEN Yüz')).toBe('gulen yuz');
    expect(fold('IŞIK')).toBe(fold('ışık'));
    expect(fold('İNEK')).toBe('inek');
    expect(fold('Çiçek Ağacı')).toBe('cicek agaci');
  });
});
