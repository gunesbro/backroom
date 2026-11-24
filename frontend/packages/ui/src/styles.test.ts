import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

// The tokens as written; the test runs in Node, where stylesheets are not loaded.
const css = readFileSync(new URL('./styles.css', import.meta.url), 'utf8');

/** The custom properties of the first rule block starting with `selector`. */
function tokens(selector: string): Map<string, string> {
  const start = css.indexOf(`${selector} {`);
  const body = css.slice(start, css.indexOf('}', start));
  return new Map(
    Array.from(body.matchAll(/(--ph-[\w-]+):\s*(#[0-9a-f]{3,6})\s*;/giu), ([, name = '', value = '']) => [
      name,
      value,
    ]),
  );
}

function luminance(hex: string): number {
  const full = hex.length === 4 ? hex.replace(/#(.)(.)(.)/u, '#$1$1$2$2$3$3') : hex;
  const channels = [1, 3, 5].map((index) => Number.parseInt(full.slice(index, index + 2), 16) / 255);
  const [r = 0, g = 0, b = 0] = channels.map((c) =>
    c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4,
  );
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (light + 0.05) / (dark + 0.05);
}

const light = tokens(':root');
const dark = new Map([...light, ...tokens(":root[data-theme='dark']")]);

describe('focus tokens', () => {
  it.each([
    ['light', light],
    ['dark', dark],
  ])('FocusColors_InThe%sTheme_ReachThreeToOne', (_theme, theme) => {
    const color = (name: string) => theme.get(name) ?? '';
    for (const background of ['--ph-panel', '--ph-page', '--ph-subtle', '--ph-sunken']) {
      expect(contrast(color('--ph-focus-border'), color(background))).toBeGreaterThanOrEqual(3);
      expect(contrast(color('--ph-accent'), color(background))).toBeGreaterThanOrEqual(3);
    }

    // A focused selected item draws its ring in the text color on the accent fill.
    expect(contrast(color('--ph-on-accent'), color('--ph-accent-fill'))).toBeGreaterThanOrEqual(3);
  });
});
