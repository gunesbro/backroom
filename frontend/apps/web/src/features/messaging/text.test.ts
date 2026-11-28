import { describe, expect, it } from 'vitest';
import { forgetSeqs, lastSeenSeq, rememberSeq } from './seq-memory';
import { normalizeMessageText, truncateQuote } from './text';

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

describe('normalizeMessageText trimming', () => {
  it('NormalizeMessageText_WithDotNetWhitespaceAtTheEnds_TrimsItLikeTheServer', () => {
    // NEL, line and paragraph separators, no-break space, ideographic space and vertical tab.
    expect(normalizeMessageText('\u0085\u2028\u2029\u00a0\u3000\u000b a \u000b\u3000')).toBe('a');
  });

  it('NormalizeMessageText_WithAVeryLongRunOfSpaces_IsFast', () => {
    const started = performance.now();

    const result = normalizeMessageText(`a${' '.repeat(200_000)}b ${' '.repeat(200_000)}`);

    // Compared by length: a failed comparison of two huge strings would take minutes to print.
    expect(result.length).toBe(200_002);
    expect(result.endsWith('b')).toBe(true);
    expect(performance.now() - started).toBeLessThan(500);
  });

  it('NormalizeMessageText_WithAZeroWidthNoBreakSpaceAtTheEnds_KeepsIt', () => {
    // JavaScript's trim() removes U+FEFF; .NET's Trim() does not, so the count must not either.
    expect(normalizeMessageText('\uFEFFa\uFEFF')).toBe('\uFEFFa\uFEFF');
    expect('\uFEFFa\uFEFF'.trim()).toBe('a');
  });
});

describe('truncateQuote', () => {
  it('TruncateQuote_ShortText_IsUnchanged', () => {
    expect(truncateQuote('hello')).toBe('hello');
  });

  it('TruncateQuote_LongText_CutsAtTwoHundredCharacters', () => {
    expect(truncateQuote('a'.repeat(250))).toBe('a'.repeat(200));
  });

  it('TruncateQuote_CutPointInsideASurrogatePair_CutsBeforeIt', () => {
    expect(truncateQuote(`${'a'.repeat(199)}😀tail`)).toBe('a'.repeat(199));
  });

  it('TruncateQuote_CutPointInsideACombiningSequence_CutsBeforeIt', () => {
    expect(truncateQuote(`${'a'.repeat(199)}e\u0301tail`)).toBe('a'.repeat(199));
  });
});
