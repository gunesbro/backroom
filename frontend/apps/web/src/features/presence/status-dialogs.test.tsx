import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadEmojiData } from '@/features/emoji';
import { LocaleContext } from '@/shared/locale';
import { StatusApiContext, type AccountStatus, type StatusApi, type StatusInput } from './api';
import { isSingleEmoji } from './status';
import { CustomStatusDialog } from './status-dialogs';

const withEmoji: AccountStatus = {
  dndUntil: null,
  customStatus: { emoji: '🎯', text: 'Focusing', expiresAt: null },
  timeZone: 'Europe/Istanbul',
  version: 3,
};

function renderDialog(status: AccountStatus | undefined) {
  const put = vi.fn((input: StatusInput) => Promise.resolve<AccountStatus>({ ...input, version: 4 }));
  const api: StatusApi = { get: () => Promise.reject(new Error('not asked')), put };
  const onClose = vi.fn();
  render(
    <LocaleContext.Provider value="en">
      <QueryClientProvider client={new QueryClient()}>
        <StatusApiContext.Provider value={api}>
          <CustomStatusDialog status={status} onClose={onClose} />
        </StatusApiContext.Provider>
      </QueryClientProvider>
    </LocaleContext.Provider>,
  );
  return { put, onClose, dialog: screen.getByRole('dialog', { name: 'Custom status' }) };
}

beforeEach(() => {
  localStorage.clear();
});

describe('CustomStatusDialog emoji', () => {
  it('Emoji_IsPickedFromTheList_NeverTyped', async () => {
    const { put, dialog } = renderDialog(undefined);
    const choose = within(dialog).getByRole('button', { name: 'Choose an emoji' });

    // No field to type an emoji into, and no way to remove one that is not there.
    expect(within(dialog).getAllByRole('textbox')).toHaveLength(1);
    expect(within(dialog).queryByRole('button', { name: 'Remove the emoji' })).toBeNull();
    expect(choose).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(choose);
    expect(choose).toHaveAttribute('aria-expanded', 'true');
    const grid = await within(dialog).findByRole('grid');
    fireEvent.click(within(grid).getByRole('button', { name: 'grinning face' }));

    // The picker closes and focus returns to the button, which now shows the emoji.
    const change = within(dialog).getByRole('button', { name: 'Change the emoji, now 😀' });
    expect(change).toHaveFocus();
    expect(change).toHaveAttribute('aria-expanded', 'false');
    expect(within(dialog).queryByRole('grid')).toBeNull();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(put.mock.calls.at(-1)?.[0].customStatus).toEqual({ emoji: '😀', text: null, expiresAt: null });
    });
  });

  it('RemoveButton_ClearsTheEmoji_AndKeepsTheText', async () => {
    const { put, dialog } = renderDialog(withEmoji);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove the emoji' }));

    expect(within(dialog).getByRole('button', { name: 'Choose an emoji' })).toHaveFocus();
    expect(within(dialog).queryByRole('button', { name: 'Remove the emoji' })).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(put.mock.calls.at(-1)?.[0].customStatus).toEqual({
        emoji: null,
        text: 'Focusing',
        expiresAt: null,
      });
    });
  });

  it('Escape_InThePicker_ClosesThePickerAndNotTheDialog', async () => {
    const { onClose, dialog } = renderDialog(withEmoji);
    const change = within(dialog).getByRole('button', { name: 'Change the emoji, now 🎯' });

    fireEvent.click(change);
    const box = await within(dialog).findByRole('searchbox', { name: 'Search emoji' });
    expect(box).toHaveFocus();
    fireEvent.keyDown(box, { key: 'Escape' });

    expect(within(dialog).queryByRole('grid')).toBeNull();
    expect(change).toHaveFocus();
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.keyDown(change, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('Dialog_WithThePickerOpen_HasNoAccessibilityViolations', async () => {
    const { dialog } = renderDialog(withEmoji);
    const rules = { rules: { 'color-contrast': { enabled: false } } };
    expect((await axe.run(document.body, rules)).violations).toEqual([]);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Change the emoji, now 🎯' }));
    await within(dialog).findByRole('grid');

    expect((await axe.run(document.body, rules)).violations).toEqual([]);
  });
});

describe('isSingleEmoji', () => {
  it('EveryEmojiOfThePicker_PassesTheClientCheck_AsItPassesTheServers', async () => {
    const data = await loadEmojiData('en');

    expect([...data.byEmoji.keys()].filter((emoji) => !isSingleEmoji(emoji))).toEqual([]);
  });

  it('Keycaps_AreOneEmoji_ButDigitsAndTextAreNot', () => {
    expect(isSingleEmoji('1️⃣')).toBe(true);
    expect(isSingleEmoji('#️⃣')).toBe(true);
    // The keycap mark alone or on a letter is not a keycap.
    expect(isSingleEmoji('⃣')).toBe(false);
    expect(isSingleEmoji('a⃣')).toBe(false);
    expect(isSingleEmoji('1')).toBe(false);
    expect(isSingleEmoji('a')).toBe(false);
    expect(isSingleEmoji('🎯🎯')).toBe(false);
  });
});
