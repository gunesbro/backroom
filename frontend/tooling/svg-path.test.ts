import { describe, expect, it } from 'vitest';
import { roundPathData, roundSvgPaths } from '../apps/web/svg-path.ts';

describe('SVG path rounding', () => {
  it('Numbers_AreRoundedAndWrittenShort', () => {
    expect(roundPathData('M10.04 20.96L-0.25,3.14159 0.04 -0.04z', 1)).toBe('M10 21L-.3 3.1 0 0z');
    expect(roundPathData('M 1.25 2 C 3.333 4.666 5 6 7.5 8.55', 1)).toBe('M1.3 2C3.3 4.7 5 6 7.5 8.6');
  });

  it('NumbersWrittenWithoutSeparators_StayApart', () => {
    // "1.96.5" is 1.96 and .5: after rounding they must not read as 2.5.
    expect(roundPathData('m1.96.5-3.04-.26', 1)).toBe('m2 .5-3-.3');
    expect(roundPathData('M1e1 2.5e-1', 1)).toBe('M10 .3');
  });

  it('ArcFlags_AreKeptAsTheyAre_AlsoWhenWrittenTogether', () => {
    expect(roundPathData('a1.26 1.26 0 011.04 2.06', 1)).toBe('a1.3 1.3 0 0 1 1 2.1');
    expect(roundPathData('A5 5 0 1 0 10.55 10a2 2 0 00.3.3', 1)).toBe('A5 5 0 1 0 10.6 10a2 2 0 0 0 .3 .3');
  });

  it('PathDataItDoesNotUnderstand_Throws', () => {
    expect(() => roundPathData('M1 2 ? 3', 1)).toThrow();
    expect(() => roundPathData('1 2', 1)).toThrow();
    expect(() => roundPathData('M1 2z3', 1)).toThrow();
  });

  it('Svg_OnlyPathDataChanges', () => {
    const svg = '<path fill="#fff" opacity="0.55" id="d1.25" d="M1.25 2.75"/><circle cx="1.25" r="2.75"/>';

    expect(roundSvgPaths(svg, 1)).toBe(
      '<path fill="#fff" opacity="0.55" id="d1.25" d="M1.3 2.8"/><circle cx="1.25" r="2.75"/>',
    );
  });
});
