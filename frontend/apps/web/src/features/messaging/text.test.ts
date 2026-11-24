import { describe, expect, it } from 'vitest';
import { forgetSeqs, lastSeenSeq, rememberSeq } from './seq-memory';
import { normalizeMessageText } from './text';

describe('normalizeMessageText', () => {
  it('NormalizeMessageText_WithLineEndingsAndSpaces_MatchesTheServer', () => {
    expect(normalizeMessageText('  a\r\nb\rc\n  ')).toBe('a\nb\nc');
  });

  it('NormalizeMessageText_WithCombiningAccent_ComposesIt', () => {
    expect(normalizeMessageText('e\u0301')).toBe('\u00e9');
  });

  it('NormalizeMessageText_WithTurkishDottedI_KeepsTheCharactersUnchanged', () => {
    expect(normalizeMessageText('İstanbul ılık')).toBe('İstanbul ılık');
  });
});

describe('seq memory', () => {
  it('RememberSeq_WithAnOlderSeq_KeepsTheNewest', () => {
    forgetSeqs();
    rememberSeq('o', 'c', 10);
    rememberSeq('o', 'c', 7);
    rememberSeq('o', 'c', 12);

    expect(lastSeenSeq('o', 'c')).toBe(12);
    expect(lastSeenSeq('o', 'other')).toBeUndefined();
  });

  it('RememberSeq_BeyondTheLimit_DropsTheLeastRecentlyAdvancedChannel', () => {
    forgetSeqs();
    for (let index = 0; index < 501; index += 1) rememberSeq('o', `c${String(index)}`, 1);

    expect(lastSeenSeq('o', 'c0')).toBeUndefined();
    expect(lastSeenSeq('o', 'c500')).toBe(1);
  });
});
