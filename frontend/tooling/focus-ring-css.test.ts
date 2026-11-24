import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// A selector list that ends in a comma joins the next rule to it (this once took the accent ring away from the
// sidebar, header and composer buttons), so the focus ring rule is checked on the file itself.
const css = readFileSync(
  fileURLToPath(new URL('../apps/web/src/features/messaging/messaging.css', import.meta.url)),
  'utf8',
).replaceAll(/\/\*[\s\S]*?\*\//g, '');

describe('Focus ring styles', () => {
  it('KeyboardFocusSelectors_FormTheirOwnRule_NotJoinedToTheSelectedChannelRule', () => {
    const ring = css.split('}').find((rule) => rule.includes('.bm-item:focus-visible,'));

    expect(ring).toBeDefined();
    expect(ring).toContain('.bm-composer__bar button:focus-visible');
    expect(ring).toContain('outline: 2px solid var(--ph-accent)');
    expect(ring).not.toContain('aria-current');
  });

  it('Selectors_NeverEndInACommaBeforeABlankLine', () => {
    expect(/,\s*\n\s*\n/.test(css)).toBe(false);
  });
});
