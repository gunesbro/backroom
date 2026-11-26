import { expect, test } from '@playwright/test';

// The message action bar in a real browser: jsdom cannot tell hover or keyboard focus from a press that focuses a row.
test('MessageActions_WhenHoveredOrFocusedByKeyboard_AreShownAndAMouseClickDoesNotPinThem', async ({
  page,
}) => {
  await page.goto('/e2e/message-list.html');
  const feed = page.getByRole('feed');
  await feed.waitFor();
  const rows = page.getByRole('article');
  const reply = page.getByRole('button', { name: 'Reply' });
  const last = rows.last();
  await expect(rows.first()).toBeVisible();

  // Hidden by default.
  await expect(reply).toHaveCount(0);

  // Shown under the pointer, hidden again when it leaves.
  await last.hover();
  await expect(last.getByRole('button', { name: 'Reply' })).toBeVisible();
  await page.mouse.move(0, 0);
  await expect(reply).toHaveCount(0);

  // A mouse click focuses the row but does not pin the bar open after the pointer leaves.
  await last.click({ position: { x: 100, y: 10 } });
  await expect(last).toBeFocused();
  await page.mouse.move(0, 0);
  await expect(reply).toHaveCount(0);

  // Keyboard focus shows it, Tab reaches its buttons, Escape returns to the row.
  await page.keyboard.press('ArrowUp');
  const row = page.locator('[data-row-key="c-198"]');
  await expect(row).toBeFocused();
  await expect(row.getByRole('button', { name: 'Reply' })).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(row.getByRole('button', { name: 'Add reaction' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(row).toBeFocused();
});
