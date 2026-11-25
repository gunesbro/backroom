import { fireEvent, render, screen, within } from '@testing-library/react';
import axe from 'axe-core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Locale } from '@backroom/i18n';
import { LocaleContext } from '@/shared/locale';
import { EmojiPicker } from './emoji-picker';

const RECENT_KEY = 'ph.emoji.recent';
const TONE_KEY = 'ph.emoji.skin-tone';

function renderPicker(locale: Locale = 'en', onClose?: () => void) {
  const onPick = vi.fn<(emoji: string) => void>();
  const view = render(
    <LocaleContext.Provider value={locale}>
      <EmojiPicker onPick={onPick} {...(onClose ? { onClose } : {})} />
    </LocaleContext.Provider>,
  );
  return { onPick, ...view };
}

const search = (value: string, name = 'Search emoji') => {
  fireEvent.change(screen.getByRole('searchbox', { name }), { target: { value } });
};

const emojiNames = (grid: HTMLElement) =>
  within(grid)
    .getAllByRole('button')
    .map((button) => button.getAttribute('aria-label'));

beforeEach(() => {
  localStorage.clear();
});
afterEach(() => {
  vi.restoreAllMocks();
});

describe('EmojiPicker', () => {
  it('Pick_FromTheFirstGroup_ReportsTheEmojiAndRemembersIt', async () => {
    const { onPick } = renderPicker();
    expect(screen.getByRole('status')).toHaveTextContent('Loading emoji…');

    const grid = await screen.findByRole('grid', { name: 'Smileys and emotion' });
    expect(screen.getByRole('searchbox', { name: 'Search emoji' })).toHaveFocus();
    fireEvent.click(within(grid).getByRole('button', { name: 'grinning face' }));

    expect(onPick).toHaveBeenCalledExactlyOnceWith('😀');
    expect(JSON.parse(localStorage.getItem(RECENT_KEY) ?? '')).toEqual(['😀']);
  });

  it('Search_ByNameOrKeyword_ShowsMatchesWithNamesThatStartWithItFirst', async () => {
    const { onPick } = renderPicker();
    await screen.findByRole('grid');

    search('  ROCK ');
    const results = screen.getByRole('grid', { name: 'Search results' });
    const names = emojiNames(results);
    expect(names).toContain('rocket');
    expect(names[0]).toBe('rock');
    expect(screen.getByRole('status')).toHaveTextContent(`${String(names.length)} emoji found`);
    // No group is the selected one while the results show.
    expect(screen.queryByRole('tab', { selected: true })).toBeNull();

    // A keyword, not part of the name.
    search('lol');
    expect(emojiNames(screen.getByRole('grid'))).toContain('face with tears of joy');

    fireEvent.keyDown(screen.getByRole('searchbox'), { key: 'Enter' });
    expect(onPick).toHaveBeenCalledTimes(1);
  });

  it('Search_WithNoMatch_SaysSoAndShowsNoGrid', async () => {
    renderPicker();
    await screen.findByRole('grid');

    search('zzzzqq');

    expect(screen.queryByRole('grid')).toBeNull();
    expect(screen.getByRole('status')).toHaveTextContent('No emoji found.');
  });

  it('Search_NeverShowsMoreThanTheLimit', async () => {
    renderPicker();
    await screen.findByRole('grid');

    search('a');

    expect(within(screen.getByRole('grid')).getAllByRole('button')).toHaveLength(96);
  });

  it('Search_InTurkish_FindsTurkishNamesWithoutAccentsAndEnglishOnes', async () => {
    renderPicker('tr');
    expect(await screen.findByRole('grid', { name: 'İfadeler ve duygular' })).toBeInTheDocument();

    // "gulen" for "gülen", upper case with the Turkish dotted İ and dotless I.
    search('gulen', 'Emoji ara');
    expect(emojiNames(screen.getByRole('grid', { name: 'Arama sonuçları' }))).toContain('gülen yüz');
    search('IŞIK', 'Emoji ara');
    expect(emojiNames(screen.getByRole('grid'))).toContain('ışık halkalı gülümseme');
    search('İNEK', 'Emoji ara');
    expect(emojiNames(screen.getByRole('grid'))).toContain('inek');

    // English still works, and the result carries its Turkish name.
    search('rocket', 'Emoji ara');
    expect(emojiNames(screen.getByRole('grid'))).toContain('roket');

    // Flags are named after their region.
    search('bayrak: türkiye', 'Emoji ara');
    expect(
      within(screen.getByRole('grid')).getByRole('button', { name: 'bayrak: Türkiye' }),
    ).toHaveTextContent('🇹🇷');
  });

  it('Keyboard_ArrowsHomeEndAndPageKeys_MoveThroughTheGridAsOneTabStop', async () => {
    renderPicker();
    const grid = await screen.findByRole('grid');
    const cells = within(grid).getAllByRole('button');
    const searchBox = screen.getByRole('searchbox');
    const press = (key: string, more: { ctrlKey?: boolean } = {}) => {
      fireEvent.keyDown(document.activeElement ?? grid, { key, ...more });
    };

    fireEvent.keyDown(searchBox, { key: 'ArrowDown' });
    expect(cells[0]).toHaveFocus();

    press('ArrowRight');
    press('ArrowRight');
    expect(cells[2]).toHaveFocus();
    press('ArrowDown');
    expect(cells[10]).toHaveFocus();
    press('ArrowLeft');
    expect(cells[9]).toHaveFocus();
    press('End');
    expect(cells[15]).toHaveFocus();
    press('Home');
    expect(cells[8]).toHaveFocus();
    press('PageDown');
    expect(cells[40]).toHaveFocus();
    press('PageUp');
    expect(cells[8]).toHaveFocus();
    press('End', { ctrlKey: true });
    expect(cells.at(-1)).toHaveFocus();
    // The edges hold.
    press('ArrowRight');
    press('ArrowDown');
    expect(cells.at(-1)).toHaveFocus();
    press('Home', { ctrlKey: true });
    expect(cells[0]).toHaveFocus();
    press('ArrowLeft');
    expect(cells[0]).toHaveFocus();

    // Only the emoji that had focus last is in the tab order.
    press('ArrowRight');
    expect(cells.filter((cell) => cell.tabIndex === 0)).toEqual([cells[1]]);

    // Up from the first row goes back to the search box.
    press('ArrowUp');
    expect(searchBox).toHaveFocus();
  });

  it('Keyboard_DownFromTheRowAboveAShorterLastRow_GoesToTheLastEmoji', async () => {
    // Eleven recent emoji: a full row and a row of three.
    const eleven = ['😀', '😃', '😄', '😁', '😆', '😅', '🤣', '😂', '🙂', '🙃', '😉'];
    localStorage.setItem(RECENT_KEY, JSON.stringify(eleven));
    renderPicker();
    const cells = within(await screen.findByRole('grid', { name: 'Recently used' })).getAllByRole('button');
    expect(cells).toHaveLength(11);

    // From the end of the first row there is nothing straight below.
    fireEvent.keyDown(screen.getByRole('searchbox'), { key: 'ArrowDown' });
    fireEvent.keyDown(cells[0] as HTMLElement, { key: 'End' });
    expect(cells[7]).toHaveFocus();
    fireEvent.keyDown(cells[7] as HTMLElement, { key: 'ArrowDown' });
    expect(cells[10]).toHaveFocus();

    // Page Down from the first row stops in the last row there is, in the same column.
    fireEvent.keyDown(cells[10] as HTMLElement, { key: 'Home', ctrlKey: true });
    fireEvent.keyDown(cells[0] as HTMLElement, { key: 'PageDown' });
    expect(cells[8]).toHaveFocus();
  });

  it('Keyboard_EnterOnAnEmoji_IsAButtonPress', async () => {
    renderPicker();
    const grid = await screen.findByRole('grid');
    const first = within(grid).getAllByRole('button')[0];

    // Enter and Space reach a real button as a click; nothing else may be needed to pick with the keyboard.
    expect(first?.tagName).toBe('BUTTON');
    expect(first).toHaveAttribute('type', 'button');
    expect(first?.closest('[role="gridcell"]')?.parentElement).toHaveAttribute('role', 'row');
  });

  it('Keyboard_EnterInTheSearchBox_NeverSubmitsTheFormAround', async () => {
    const submitted = vi.fn((event: Event) => {
      event.preventDefault();
    });
    const onPick = vi.fn();
    render(
      <form onSubmit={submitted as unknown as () => void}>
        <EmojiPicker onPick={onPick} />
      </form>,
    );
    await screen.findByRole('grid');
    const box = screen.getByRole('searchbox');

    // React's keyDown must have been stopped for the browser not to submit; with a query it picks instead.
    expect(fireEvent.keyDown(box, { key: 'Enter' })).toBe(false);
    expect(onPick).not.toHaveBeenCalled();
    search('rocket');
    expect(fireEvent.keyDown(box, { key: 'Enter' })).toBe(false);
    expect(onPick).toHaveBeenCalledExactlyOnceWith('🚀');
    expect(within(screen.getByRole('grid')).getAllByRole('button')[0]).toHaveAttribute('type', 'button');
    expect(submitted).not.toHaveBeenCalled();
  });

  it('Escape_ClearsASearchFirstAndThenCloses', async () => {
    const onClose = vi.fn();
    renderPicker('en', onClose);
    await screen.findByRole('grid');
    const box = screen.getByRole('searchbox');

    search('rocket');
    fireEvent.keyDown(box, { key: 'Escape' });
    expect(box).toHaveValue('');
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.keyDown(box, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('Tabs_ArrowKeysAndClicks_ChangeTheGroup', async () => {
    renderPicker();
    await screen.findByRole('grid', { name: 'Smileys and emotion' });
    const tabs = screen.getAllByRole('tab');
    expect(tabs.map((tab) => tab.getAttribute('aria-label'))).toEqual([
      'Smileys and emotion',
      'People and body',
      'Animals and nature',
      'Food and drink',
      'Travel and places',
      'Activities',
      'Objects',
      'Symbols',
      'Flags',
    ]);
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');

    fireEvent.keyDown(tabs[0] as HTMLElement, { key: 'ArrowRight' });
    expect(tabs[1]).toHaveFocus();
    expect(tabs[1]).toHaveAttribute('aria-selected', 'true');
    expect(tabs.filter((tab) => tab.tabIndex === 0)).toEqual([tabs[1]]);
    expect(screen.getByRole('grid', { name: 'People and body' })).toBeInTheDocument();

    fireEvent.keyDown(tabs[1] as HTMLElement, { key: 'End' });
    expect(within(screen.getByRole('grid', { name: 'Flags' })).getAllByRole('button').length).toBeGreaterThan(
      200,
    );
    fireEvent.keyDown(tabs.at(-1) as HTMLElement, { key: 'ArrowRight' });
    expect(tabs[0]).toHaveFocus();

    // Choosing a group ends a search.
    search('rocket');
    fireEvent.click(screen.getByRole('tab', { name: 'Objects' }));
    expect(screen.getByRole('searchbox')).toHaveValue('');
    expect(screen.getByRole('grid', { name: 'Objects' })).toBeInTheDocument();
  });

  it('Recent_StoredEmoji_ComeFirstNewestFirst', async () => {
    localStorage.setItem(RECENT_KEY, JSON.stringify(['🚀', '👍🏽', 'not an emoji', '🚀', 7]));
    const { onPick } = renderPicker();

    const grid = await screen.findByRole('grid', { name: 'Recently used' });
    // Known emoji only, each once, as they were picked (with the skin tone).
    expect(
      within(grid)
        .getAllByRole('button')
        .map((button) => button.textContent),
    ).toEqual(['🚀', '👍🏽']);
    expect(screen.getAllByRole('tab')[0]).toHaveAttribute('aria-label', 'Recently used');

    fireEvent.click(within(grid).getByRole('button', { name: 'thumbs up' }));
    expect(onPick).toHaveBeenCalledExactlyOnceWith('👍🏽');
    expect(
      within(grid)
        .getAllByRole('button')
        .map((button) => button.textContent),
    ).toEqual(['👍🏽', '🚀']);
  });

  it('Recent_AfterManyPicks_KeepsOnlyTheLastTwentyFour', async () => {
    renderPicker();
    const grid = await screen.findByRole('grid', { name: 'Smileys and emotion' });
    const cells = within(grid).getAllByRole('button').slice(0, 30);

    for (const cell of cells) fireEvent.click(cell);

    const stored = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '') as string[];
    expect(stored).toHaveLength(24);
    expect(stored[0]).toBe(cells[29]?.textContent);
  });

  it('Recent_WhenStorageIsBrokenOrBlocked_ThePickerStillWorks', async () => {
    localStorage.setItem(RECENT_KEY, '{broken');
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('blocked', 'QuotaExceededError');
    });
    const { onPick } = renderPicker();

    const grid = await screen.findByRole('grid', { name: 'Smileys and emotion' });
    fireEvent.click(within(grid).getByRole('button', { name: 'grinning face' }));

    expect(onPick).toHaveBeenCalledExactlyOnceWith('😀');
  });

  it('SkinTone_WhenChosen_AppliesToEmojiThatHaveOneAndIsRemembered', async () => {
    const { onPick, unmount } = renderPicker();
    await screen.findByRole('grid');
    fireEvent.click(screen.getByRole('tab', { name: 'People and body' }));

    fireEvent.change(screen.getByRole('combobox', { name: 'Skin tone' }), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: 'waving hand' }));
    expect(onPick).toHaveBeenLastCalledWith('👋🏿');
    expect(localStorage.getItem(TONE_KEY)).toBe('5');

    // An emoji without skin tones stays as it is.
    search('rocket');
    expect(screen.getByRole('button', { name: 'rocket' })).toHaveTextContent('🚀');

    unmount();
    renderPicker();
    expect(await screen.findByRole('combobox', { name: 'Skin tone' })).toHaveValue('5');
  });

  it.each(['en', 'tr'] as const)('Picker_In_%s_HasNoAccessibilityViolations', async (locale) => {
    localStorage.setItem(RECENT_KEY, JSON.stringify(['🚀']));
    const { container } = renderPicker(locale);
    await screen.findByRole('grid');
    const rules = { rules: { 'color-contrast': { enabled: false } } };

    expect((await axe.run(container, rules)).violations).toEqual([]);

    fireEvent.click(screen.getAllByRole('tab')[2] as HTMLElement);
    expect((await axe.run(container, rules)).violations).toEqual([]);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'zzzzqq' } });
    expect((await axe.run(container, rules)).violations).toEqual([]);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'a' } });
    expect((await axe.run(container, rules)).violations).toEqual([]);
  });
});
