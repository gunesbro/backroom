import { describe, expect, it } from 'vitest';
import { emojiImageKey, emojiImageUrl } from './images';
import { firstEmoji, isEmojiOnly, splitEmoji } from './segment';

const emojiIn = (text: string) =>
  splitEmoji(text).flatMap((part) => (typeof part === 'string' ? [] : [part.emoji]));

describe('Emoji segmentation', () => {
  it('Text_WithoutEmoji_IsOnePart', () => {
    expect(splitEmoji('Hello, world 123 #1 * 2')).toEqual(['Hello, world 123 #1 * 2']);
    expect(splitEmoji('')).toEqual([]);
  });

  it('Text_AroundEmoji_StaysTextInOrder', () => {
    expect(splitEmoji('Hi 😀, done 🎉!')).toEqual(['Hi ', { emoji: '😀' }, ', done ', { emoji: '🎉' }, '!']);
    expect(splitEmoji('😀😀')).toEqual([{ emoji: '😀' }, { emoji: '😀' }]);
  });

  it('JoinedSequences_AreOneEmoji', () => {
    const family = '👨\u200D👩\u200D👧\u200D👦';
    const technologist = '👩🏽\u200D💻';
    const rainbowFlag = '🏳\uFE0F\u200D🌈';
    const heartOnFire = '❤\uFE0F\u200D🔥';
    const couple = '👩🏻\u200D❤\uFE0F\u200D💋\u200D👨🏿';

    expect(emojiIn(`a${family}b${technologist}c${rainbowFlag}${heartOnFire}${couple}`)).toEqual([
      family,
      technologist,
      rainbowFlag,
      heartOnFire,
      couple,
    ]);
    // Without the selectors (as some keyboards send them) it is still one emoji.
    expect(emojiIn('🏳\u200D🌈')).toEqual(['🏳\u200D🌈']);
  });

  it('SkinTones_BelongToTheirEmoji_AndOnlyToOnesThatTakeThem', () => {
    expect(emojiIn('👍🏽👋🏿')).toEqual(['👍🏽', '👋🏿']);
    // A rocket takes no skin tone: the tone is an emoji of its own.
    expect(emojiIn('🚀🏽')).toEqual(['🚀', '🏽']);
  });

  it('Keycaps_AreOneEmoji_WhileDigitsAndSignsStayText', () => {
    expect(splitEmoji('Press 1\uFE0F\u20E3 or #\uFE0F\u20E3, not 1 or #')).toEqual([
      'Press ',
      { emoji: '1\uFE0F\u20E3' },
      ' or ',
      { emoji: '#\uFE0F\u20E3' },
      ', not 1 or #',
    ]);
    expect(emojiIn('*\u20E3')).toEqual(['*\u20E3']);
  });

  it('Flags_ArePairsOfLetters_AndPartsOfCountriesAreTagSequences', () => {
    const england = '🏴\u{E0067}\u{E0062}\u{E0065}\u{E006E}\u{E0067}\u{E007F}';

    expect(emojiIn('🇹🇷🇩🇪')).toEqual(['🇹🇷', '🇩🇪']);
    expect(emojiIn(`${england}🏴`)).toEqual([england, '🏴']);
    // One letter on its own is not a flag.
    expect(splitEmoji('🇹')).toEqual(['🇹']);
    expect(splitEmoji('🇹🇷🇩')).toEqual([{ emoji: '🇹🇷' }, '🇩']);
  });

  it('TextSymbols_AreEmojiOnlyWithTheEmojiSelector', () => {
    // A heart, the copyright sign and an arrow are text by default.
    expect(splitEmoji('I ❤ you © ↔ ™')).toEqual(['I ❤ you © ↔ ™']);
    expect(emojiIn('I ❤\uFE0F you ©\uFE0F')).toEqual(['❤\uFE0F', '©\uFE0F']);
    // The text selector keeps even an emoji as text.
    expect(splitEmoji('⌚\uFE0E')).toEqual(['⌚\uFE0E']);
    expect(emojiIn('⌚')).toEqual(['⌚']);
    // A joined sequence with the text selector is text as a whole, not an emoji made of its first part.
    const family = '👨\u200D👩\u200D👧';
    expect(splitEmoji(`${family}\uFE0E`)).toEqual([`${family}\uFE0E`]);
    expect(splitEmoji(`🇹🇷\uFE0E`)).toEqual([`🇹🇷\uFE0E`]);
    expect(emojiIn(`${family}\uFE0E😀`)).toEqual(['😀']);
  });

  it('FirstEmoji_SaysWhereItIs', () => {
    expect(firstEmoji('ab👍🏽cd😀')).toEqual({ emoji: '👍🏽', start: 2, end: 6 });
    expect(firstEmoji('no emoji ❤ here')).toBeUndefined();
  });

  it('EmojiOnly_IsAFewEmojiAndSpaces', () => {
    expect(isEmojiOnly('😀')).toBe(true);
    expect(isEmojiOnly(' 🎉 👍🏽\n🇹🇷 ')).toBe(true);
    expect(isEmojiOnly('😀 ok')).toBe(false);
    expect(isEmojiOnly('')).toBe(false);
    expect(isEmojiOnly('   ')).toBe(false);
    expect(isEmojiOnly('😀'.repeat(12))).toBe(true);
    expect(isEmojiOnly('😀'.repeat(13))).toBe(false);
  });

  it('LongText_IsSplitInLinearTime', () => {
    const text = `${'👩\u200D'.repeat(20_000)}x${'1\uFE0F'.repeat(20_000)}`;
    const started = performance.now();

    splitEmoji(text);

    expect(performance.now() - started).toBeLessThan(1000);
  });
});

describe('Emoji image names', () => {
  it('Key_IsTheCodePointsInHex_WithoutTheEmojiSelector', () => {
    expect(emojiImageKey('😀')).toBe('1f600');
    expect(emojiImageKey('👍🏽')).toBe('1f44d-1f3fd');
    expect(emojiImageKey('🇹🇷')).toBe('1f1f9-1f1f7');
    expect(emojiImageKey('1\uFE0F\u20E3')).toBe('31-20e3');
    expect(emojiImageKey('#\u20E3')).toBe('23-20e3');
    expect(emojiImageKey('👨\u200D👩\u200D👧')).toBe('1f468-200d-1f469-200d-1f467');
    expect(emojiImageKey('🏴\u{E0067}\u{E0062}\u{E0065}\u{E006E}\u{E0067}\u{E007F}')).toBe(
      '1f3f4-e0067-e0062-e0065-e006e-e0067-e007f',
    );
  });

  it('Key_IsTheSame_WithAndWithoutTheEmojiSelector', () => {
    expect(emojiImageKey('❤\uFE0F')).toBe('2764');
    expect(emojiImageKey('❤')).toBe('2764');
    expect(emojiImageKey('🏳\uFE0F\u200D🌈')).toBe('1f3f3-200d-1f308');
    expect(emojiImageKey('🏳\u200D🌈')).toBe('1f3f3-200d-1f308');
    expect(emojiImageKey('🕵\uFE0F\u200D♂\uFE0F')).toBe(emojiImageKey('🕵\u200D♂'));
  });

  it('Url_IsAFileOfTheAppItself', () => {
    expect(emojiImageUrl('👍🏽')).toBe('/assets/emoji-test/1f44d-1f3fd.svg');
  });
});
