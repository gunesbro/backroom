import { describe, expect, it } from 'vitest';
import fixture from '../../../../../../docs/contracts/test-data/picker-emoji.json';
import { fold, GROUP_KEYS, loadEmojiData, searchEmojis } from './data';

describe('Emoji data', () => {
  it('Groups_AreTheNineOfThePickerInOrder_WithoutSkinToneOrHairComponents', async () => {
    const data = await loadEmojiData('en');

    expect(data.groups.map((group) => group.key)).toEqual(GROUP_KEYS);
    for (const group of data.groups) expect(group.emojis.length).toBeGreaterThan(50);
    expect(data.byEmoji.has('🏻')).toBe(false);
    expect(data.byEmoji.has('🦰')).toBe(false);
  });

  // The server's own tests accept every emoji in the fixture's "offered" list and refuse every one in "refused"
  // (AccountStatusEmojiListTests in Identity's tests), so this test is what ties the picker to the server's rule.
  it('OfferedEmoji_AreExactlyTheListTheServerIsTestedAgainst', async () => {
    const data = await loadEmojiData('en');
    const offered = Object.fromEntries(
      data.groups.map((group) => [
        group.key,
        group.emojis.flatMap((emoji) => [emoji.emoji, ...(emoji.skins ?? [])]).join(' '),
      ]),
    );

    // On a difference: run `pnpm -C frontend update:emoji-fixture`, then the backend test.
    expect(offered).toEqual(fixture.offered);
    // About 1,900 emoji and five skin tones for some 330 of them.
    expect(data.byEmoji.size).toBeGreaterThan(3400);
  });

  it('EmojiTheServerRefuses_AreNeverOffered', async () => {
    const data = await loadEmojiData('en');

    expect(fixture.refused.length).toBeGreaterThan(10);
    expect(fixture.refused.filter((sample) => data.byEmoji.has(sample.value))).toEqual([]);
  });

  it('EveryEmojiAndSkinTone_FitsTheReactionLimit', async () => {
    const data = await loadEmojiData('en');
    const bytes = new TextEncoder();

    // A reaction is at most 32 bytes of UTF-8 (docs/contracts/phase-2.md).
    expect([...data.byEmoji.keys()].filter((emoji) => bytes.encode(emoji).length > 32)).toEqual([]);
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
