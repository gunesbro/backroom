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

// The "more" menu hangs out of its row over the next one; rows are stacking contexts of their own.
test('MessageMenu_WhenOpenedOverTheNextRow_StaysAboveItAndTakesThePointer', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 800 });
  await page.goto('/e2e/message-list.html');
  await page.getByRole('feed').waitFor();
  // Own messages are the even ones (authors alternate); two in a row are not adjacent, so use the row under the first.
  const upper = page.locator('[data-row-key="c-198"]');
  await upper.scrollIntoViewIfNeeded();
  await upper.hover();
  await upper.getByRole('button', { name: 'More' }).click();
  const menu = page.getByRole('menu');
  const item = menu.getByRole('menuitem').last();
  await expect(item).toBeVisible();

  const box = await item.boundingBox();
  if (!box) throw new Error('The menu item has no box.');
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);

  // The menu item, not the next row, is under the pointer, and the next row shows no action bar.
  const underPointer = await page.evaluate(
    ([px, py]) => document.elementFromPoint(px ?? 0, py ?? 0)?.closest('[role="menuitem"]') !== null,
    [x, y],
  );
  expect(underPointer).toBe(true);
  await expect(page.locator('[data-row-key="c-199"]').getByRole('button', { name: 'Reply' })).toHaveCount(0);
  await expect(item).toBeVisible();

  // Escape closes it and the row is back at its place in the stack.
  await page.keyboard.press('Escape');
  await expect(menu).toHaveCount(0);
});

// The last message is at the bottom of the scroll container: its menu must flip upward and stay inside it.
test('MessageMenu_WhenOpenedOnTheLastMessageOfALongList_OpensUpwardInsideTheList', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 800 });
  await page.goto('/e2e/message-list.html?own=last');
  await page.getByRole('feed').waitFor();
  const last = page.getByRole('article').last();
  await last.scrollIntoViewIfNeeded();
  await last.hover();
  const more = last.getByRole('button', { name: 'More' });
  await more.click();
  const menu = page.getByRole('menu');
  await expect(menu).toBeVisible();
  const log = await page.getByRole('feed').boundingBox();
  const box = await menu.boundingBox();
  const button = await more.boundingBox();
  if (!log || !box || !button) throw new Error('No box.');
  expect(box.y + box.height).toBeLessThanOrEqual(button.y + 1);
  expect(box.y).toBeGreaterThanOrEqual(log.y);
});

// In a short list there is no room above the first message: the menu stays below it.
test('MessageMenu_WhenOpenedInAOneMessageList_OpensDownward', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 800 });
  await page.goto('/e2e/message-list.html?count=1');
  await page.getByRole('feed').waitFor();
  const only = page.getByRole('article').first();
  await only.hover();
  const more = only.getByRole('button', { name: 'More' });
  await more.click();
  const box = await page.getByRole('menu').boundingBox();
  const button = await more.boundingBox();
  if (!box || !button) throw new Error('No box.');
  expect(box.y).toBeGreaterThanOrEqual(button.y + button.height);
});

// A focused row must not lift over the next row's action bar, which hangs up into it.
test('MessageActions_WhenTheRowAboveIsFocused_TheBarOfTheHoveredRowStaysOnTop', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 800 });
  await page.goto('/e2e/message-list.html');
  await page.getByRole('feed').waitFor();
  const upper = page.locator('[data-row-key="c-198"]');
  const lower = page.locator('[data-row-key="c-199"]');
  await upper.scrollIntoViewIfNeeded();
  await upper.click({ position: { x: 100, y: 10 } });
  await expect(upper).toBeFocused();
  await lower.hover();
  const bar = lower.locator('.bm-actions');
  await expect(bar).toBeVisible();
  const box = await bar.boundingBox();
  if (!box) throw new Error('No box.');
  const x = box.x + box.width / 2;
  const y = box.y + 3;
  const inside = await page.evaluate(
    ([px, py]) => document.elementFromPoint(px ?? 0, py ?? 0)?.closest('.bm-actions') !== null,
    [x, y],
  );
  expect(inside).toBe(true);
});
