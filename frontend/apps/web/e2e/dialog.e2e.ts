/// <reference lib="dom" />
// The callbacks of evaluate() run in the browser page.
import { expect, test } from '@playwright/test';

// The shared modal dialog in a real browser: what the jsdom stand-in cannot show (the top layer, the inert page,
// focus, the browser's own close request and the layout).
test('Dialog_WhenOpenedAndClosedWithTheKeyboard_IsModalKeepsFocusAndClosesOnce', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/e2e/dialog.html');
  const opener = page.getByRole('button', { name: 'Open dialog' });
  await opener.click();

  const dialog = page.getByRole('dialog', { name: 'Smoke dialog' });
  await expect(dialog).toBeVisible();
  expect(await dialog.evaluate((element) => element.matches(':modal'))).toBe(true);
  // Focus goes to the chosen field, and the page behind is inert.
  await expect(page.getByLabel('Name')).toBeFocused();
  const behind = page.getByRole('button', { name: 'Behind the dialog', includeHidden: true });
  const point = await behind.boundingBox();
  const hit = await page.evaluate(
    ([x, y]) => document.elementFromPoint(x ?? 0, y ?? 0)?.textContent ?? '',
    [(point?.x ?? 0) + 2, (point?.y ?? 0) + 2],
  );
  expect(hit).not.toBe('Behind the dialog');
  // Tab never reaches the page behind (past the last control it may visit the browser's own controls).
  for (let step = 0; step < 12; step += 1) {
    await page.keyboard.press('Tab');
    expect(await page.evaluate(() => document.activeElement?.textContent), `step ${String(step)}`).not.toBe(
      'Behind the dialog',
    );
  }

  // Centered, 420 px wide, over a backdrop.
  const box = await dialog.boundingBox();
  expect(box?.width).toBe(420);
  expect(Math.abs((box?.x ?? 0) + (box?.width ?? 0) / 2 - 640)).toBeLessThan(1);
  expect(
    await dialog.evaluate((element) => getComputedStyle(element, '::backdrop').backgroundColor),
  ).not.toBe('rgba(0, 0, 0, 0)');

  // Escape in the emoji search clears the text and leaves the dialog open.
  const search = page.getByRole('searchbox');
  await search.fill('rock');
  await search.press('Escape');
  await expect(search).toHaveValue('');
  await expect(dialog).toBeVisible();

  // The next Escape closes the dialog once and gives focus back to what opened it.
  await search.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(page.getByLabel('Times closed')).toHaveText('1');
  await expect(opener).toBeFocused();
});
