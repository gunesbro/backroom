import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { expect, test } from '@playwright/test';

const LONG = `wide = '${'x'.repeat(200)}'`;

// One pass through the rich text composer in a real browser: what jsdom cannot show (layout, the caret, real key
// events). The editing rules are covered in detail by the Vitest suites of features/messaging.
test('Composer_WhenALongMessageIsTypedWithTheKeyboard_SendsItsBlocksAndDrawsThem', async ({ page }) => {
  await page.goto('/e2e/composer.html');
  const field = page.getByRole('textbox', { name: 'Message #general' });
  await field.click();

  // The caret's line and the placeholder are the same line, in both themes.
  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    const placeholder = await page.locator('.bm-rt__placeholder').boundingBox();
    const line = await field.locator('p').first().boundingBox();
    expect(placeholder, theme).not.toBeNull();
    expect(Math.abs((line?.y ?? 0) - (placeholder?.y ?? -100)), theme).toBeLessThan(1);
    expect(Math.abs((line?.x ?? 0) - (placeholder?.x ?? -100)), theme).toBeLessThan(1);
    expect(Math.abs((line?.height ?? 0) - (placeholder?.height ?? -100)), theme).toBeLessThan(1);
  }

  const line = async (text: string) => {
    await page.keyboard.type(text);
    await page.keyboard.press('Shift+Enter');
  };
  await line('hello');
  await line('- one');
  await line('two');
  await page.keyboard.press('Shift+Enter'); // the empty item: out of the list
  await line('> quoted');
  await line('more');
  await page.keyboard.press('Shift+Enter'); // the empty quoted line: out of the quote
  await page.keyboard.type('```');
  await line('a = 1');
  await line('  b = 2');
  await page.keyboard.type(LONG);
  await expect(field.locator('.bm-code-block')).toHaveText(/a = 1\s+b = 2/);
  await page.keyboard.type('```'); // closes the block
  await page.keyboard.type('see `x` there');
  await expect(page.locator('.bm-rt__placeholder')).toHaveCount(0);

  await page.keyboard.press('Enter');

  const message = page.getByRole('list', { name: 'Sent messages' }).getByRole('listitem').first();
  await expect(message).toBeVisible();
  expect(JSON.parse((await message.getAttribute('data-blocks')) ?? '[]')).toEqual([
    { type: 'paragraph', children: [{ type: 'text', text: 'hello' }] },
    {
      type: 'bulleted_list',
      children: [
        { type: 'list_item', children: [{ type: 'text', text: 'one' }] },
        { type: 'list_item', children: [{ type: 'text', text: 'two' }] },
      ],
    },
    { type: 'quote', children: [{ type: 'text', text: 'quoted\nmore' }] },
    { type: 'code_block', children: [{ type: 'text', text: `a = 1\n  b = 2\n${LONG}` }] },
    {
      type: 'paragraph',
      children: [
        { type: 'text', text: 'see ' },
        { type: 'text', text: 'x', marks: [{ type: 'code' }] },
        { type: 'text', text: ' there' },
      ],
    },
  ]);

  // The message is drawn with markers, a quote bar and a code box that scrolls instead of wrapping.
  await expect(message.locator('.bm-blocks')).toHaveCSS('display', 'block'); // blocks below each other
  const items = message.locator('ul > li');
  await expect(items).toHaveCount(2);
  await expect(items.first()).toHaveCSS('list-style-type', 'disc');
  await expect(items.first()).toHaveCSS('display', 'list-item');
  await expect(message.locator('blockquote')).toHaveCSS('border-left-width', '3px');
  const code = message.locator('pre');
  await expect(code).toHaveCSS('white-space', 'pre');
  await expect(code).toHaveCSS('overflow-x', 'auto');
  await expect(code).toHaveCSS('border-top-width', '1px');
  await expect(message.locator('code.bm-inline-code')).toHaveCSS('border-top-width', '1px');

  // The box really scrolls here, so axe can judge it: a scrollable region must be reachable with the keyboard.
  const scrolls = await code.evaluate((element) => {
    // This file is typed for Node (no DOM library), so the element's sizes are named here.
    const box = element as unknown as { scrollWidth: number; clientWidth: number };
    return box.scrollWidth > box.clientWidth;
  });
  expect(scrolls).toBe(true);
  await expect(page.getByRole('region', { name: 'Code block' })).toHaveAttribute('tabindex', '0');
  // Evaluated, not added as a script element: the page enforces Trusted Types, and a script element's text is a sink.
  await page.evaluate(readFileSync(createRequire(import.meta.url).resolve('axe-core/axe.min.js'), 'utf8'));
  const violations = await page.evaluate(`
    axe
      .run('[aria-label="Sent messages"]', { runOnly: ['scrollable-region-focusable', 'aria-allowed-role'] })
      .then((results) => results.violations.map((violation) => violation.id))
  `);
  expect(violations).toEqual([]);

  // The field is empty again and still has the keyboard.
  await expect(page.locator('.bm-rt__placeholder')).toBeVisible();
  await expect(field).toBeFocused();
});
