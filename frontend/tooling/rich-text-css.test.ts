// The rich text composer and message styles, read from the style sheets (jsdom lays nothing out and Vitest loads CSS
// as empty): the rules that put the caret on the placeholder's line and that draw quotes, lists and code, and the
// contrast of their colours in both themes (WCAG AA).
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

function read(path: string): string {
  return readFileSync(fileURLToPath(new URL(path, import.meta.url)), 'utf8').replaceAll('\r\n', '\n');
}

const tokensCss = read('../packages/ui/src/styles.css');
const css = read('../apps/web/src/features/messaging/messaging.css');
const messageBlocks = read('../apps/web/src/features/messaging/rich-text/message-blocks.tsx');

/** The declarations of the rule with exactly this selector list. */
function rule(sheet: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/,\s*/g, ',\\s*');
  // The selector is a literal of this test, escaped above, matched against our own style sheets: no untrusted input.
  // nosemgrep: javascript.lang.security.audit.detect-non-literal-regexp.detect-non-literal-regexp
  const match = new RegExp(`(?:^|})\\s*(?:/\\*[^]*?\\*/\\s*)*${escaped}\\s*{([^}]*)}`, 'm').exec(sheet);
  if (!match) throw new Error(`no rule for ${selector}`);
  const declarations: Record<string, string> = {};
  for (const line of (match[1] ?? '').split(';')) {
    const colon = line.indexOf(':');
    if (colon > 0) declarations[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return declarations;
}

type Rgb = [number, number, number];

function tokens(selector: string): Map<string, string> {
  const start = tokensCss.indexOf(`${selector} {`);
  if (start === -1) throw new Error(`no ${selector}`);
  const body = tokensCss.slice(start, tokensCss.indexOf('}', start));
  const values = new Map<string, string>();
  for (const [, name, value] of body.matchAll(/(--ph-[\w-]+):\s*([^;]+);/g)) {
    if (name && value) values.set(name, value);
  }
  return values;
}

const LIGHT = tokens(':root');
const DARK = new Map([...LIGHT, ...tokens(":root[data-theme='dark']")]);

function color(theme: Map<string, string>, token: string): Rgb {
  const value = theme.get(token) ?? '';
  const hex = /^#([\da-f]{3}|[\da-f]{6})$/i.exec(value)?.[1];
  if (!hex) throw new Error(`${token} is not a hex colour: ${value}`);
  const full = hex.length === 3 ? hex.replace(/./g, '$&$&') : hex;
  return [0, 2, 4].map((at) => Number.parseInt(full.slice(at, at + 2), 16)) as Rgb;
}

function mix(a: Rgb, share: number, b: Rgb): Rgb {
  return a.map((channel, index) => channel * share + (b[index] ?? 0) * (1 - share)) as Rgb;
}

function luminance(rgb: Rgb): number {
  const [r, g, b] = rgb.map((channel) => {
    const value = channel / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  }) as Rgb;
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: Rgb, b: Rgb): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (light + 0.05) / (dark + 0.05);
}

describe('Composer caret and placeholder', () => {
  it('Placeholder_WhenTheFieldIsEmpty_LiesOnTheFirstLine', () => {
    const input = rule(css, '.bm-composer__input');
    const placeholder = rule(css, '.bm-rt__placeholder');
    // The first paragraph must not bring the browser's 1em margin: that pushed the caret a line below.
    expect(rule(css, '.bm-rt-line').margin).toBe('0');
    expect(placeholder['font-size']).toBe(input['font-size']);
    expect(placeholder['line-height']).toBe(input['line-height']);
    expect(placeholder.position).toBe('absolute');
    const [top, sides] = (input.padding ?? '').split(' ');
    expect(placeholder.inset).toBe(`${String(top)} ${String(sides)} auto`);
  });
});

describe('Rich text styles', () => {
  it('InlineCode_WhenStyled_IsASmallBorderedMonospaceChip', () => {
    const shared = rule(css, '.bm-inline-code, .bm-code-block');
    const inline = rule(css, '.bm-inline-code');
    expect(shared['font-family']).toContain('monospace');
    expect(shared.border).toBe('1px solid var(--ph-line)');
    expect(shared.background).toBe('var(--ph-sunken)');
    expect(inline['border-radius']).toBe('var(--ph-radius-xs)');
    expect(inline.color).toBe('var(--ph-accent-strong)');
    expect(Number.parseFloat(inline['font-size'] ?? '')).toBeLessThan(1);
  });

  it('CodeBlock_WhenStyled_IsAFullWidthBoxThatScrollsInsteadOfWrapping', () => {
    const block = rule(css, '.bm-code-block');
    expect(block).toMatchObject({
      display: 'block',
      width: '100%',
      'box-sizing': 'border-box',
      'white-space': 'pre',
      'overflow-x': 'auto',
      'border-radius': 'var(--ph-radius-sm)',
    });
    expect(block.padding).toBeTruthy();
  });

  it('QuoteAndLists_WhenStyled_HaveABarMarkersAndIndentation', () => {
    const quote = rule(css, '.bm-quote');
    expect(quote['border-left']).toContain('3px solid');
    expect(quote.color).toBe('var(--ph-muted)');
    expect(quote.padding).toBeTruthy();
    expect(rule(css, '.bm-rt-list')['padding-left']).toBe('var(--ph-space-24)');
    expect(rule(css, 'ul.bm-rt-list')['list-style']).toContain('disc');
    expect(rule(css, 'ol.bm-rt-list')['list-style']).toContain('decimal');
  });

  // Both names were taken by the layout: .bm-body is the workspace's flex row (a message's blocks stood side by
  // side) and .bm-list the sidebar's channel list (no markers).
  it('Message_WhenDrawnFromBlocks_DoesNotShareAClassWithTheLayout', () => {
    expect(messageBlocks).toContain("'bm-blocks'");
    expect(messageBlocks).not.toMatch(/bm-body|bm-list/);
    expect(css.match(/^\.bm-blocks \{/gm)).toHaveLength(1);
    expect(rule(css, '.bm-blocks').display).toBeUndefined();
    expect(css.match(/^\.bm-rt-list \{/gm)).toHaveLength(1);
  });

  it.each([
    ['light', LIGHT],
    ['dark', DARK],
  ] as const)('Colours_WhenTheThemeIs%s_MeetWcagAa', (_name, theme) => {
    const panel = color(theme, '--ph-panel');
    const sunken = color(theme, '--ph-sunken');
    // Text: 4.5:1.
    expect(contrast(color(theme, '--ph-accent-strong'), sunken)).toBeGreaterThanOrEqual(4.5); // inline code
    expect(contrast(color(theme, '--ph-ink'), sunken)).toBeGreaterThanOrEqual(4.5); // code block
    expect(contrast(color(theme, '--ph-muted'), panel)).toBeGreaterThanOrEqual(4.5); // quote
    // The quote's bar is the only shape that marks a quote: 3:1 (it mixes 70% muted into the panel).
    expect(rule(css, '.bm-quote')['border-left']).toContain(
      'color-mix(in srgb, var(--ph-muted) 70%, var(--ph-panel))',
    );
    expect(contrast(mix(color(theme, '--ph-muted'), 0.7, panel), panel)).toBeGreaterThanOrEqual(3);
  });
});
