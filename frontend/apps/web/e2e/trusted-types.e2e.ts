/// <reference lib="dom" />
// The callbacks of evaluate() run in the browser page.
import { expect, test } from '@playwright/test';

// The smoke pages get the same Trusted Types rule as the built app (trusted-types.ts).
test('TrustedTypes_WhenAStringReachesAnHtmlSink_IsBlocked', async ({ page }) => {
  await page.goto('/e2e/composer.html');
  await expect(page.getByRole('textbox', { name: 'Message #general' })).toBeVisible();

  const outcome = await page.evaluate(() => {
    try {
      document.createElement('div').innerHTML = '<b>x</b>';
      return 'allowed';
    } catch (error) {
      return error instanceof TypeError ? 'blocked' : 'other error';
    }
  });

  expect(outcome).toBe('blocked');
});

test('TrustedTypes_WhenHtmlIsPastedIntoTheComposer_KeepsItsFormatting', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') errors.push(message.text());
  });
  await page.goto('/e2e/composer.html');
  const field = page.getByRole('textbox', { name: 'Message #general' });
  await field.click();

  // Only HTML on the clipboard, so the editor's own HTML import (and its policy) does the work.
  await field.evaluate((element) => {
    const data = new DataTransfer();
    data.setData('text/html', '<p><b>bold</b> and plain</p>');
    element.dispatchEvent(
      new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }),
    );
  });
  await expect(field.locator('strong, b')).toHaveText('bold');
  await page.keyboard.press('Enter');

  const message = page.getByRole('list', { name: 'Sent messages' }).getByRole('listitem').first();
  expect(JSON.parse((await message.getAttribute('data-blocks')) ?? '[]')).toEqual([
    {
      type: 'paragraph',
      children: [
        { type: 'text', text: 'bold', marks: [{ type: 'bold' }] },
        { type: 'text', text: ' and plain' },
      ],
    },
  ]);
  expect(errors).toEqual([]);
});
