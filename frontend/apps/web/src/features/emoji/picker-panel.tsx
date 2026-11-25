import { useEffect, useId, useMemo, useRef, useState, type FocusEvent, type KeyboardEvent } from 'react';
import { useTranslate } from '@/shared/locale';
import { searchEmojis, type Emoji, type EmojiData, type EmojiGroupKey } from './data';
import { messages } from './messages';
import { addRecent, readRecent, readSkinTone, writeSkinTone, type SkinTone } from './recents';
import { supportsFlagEmoji } from './support';
import './emoji.css';

/** Emoji per row. Fixed, so the rows of the grid and the arrow keys always agree. */
export const COLUMNS = 8;

/** Rows moved by Page Up and Page Down. */
const PAGE_ROWS = 4;

const MAX_QUERY = 50;

type Tab = 'recent' | EmojiGroupKey;

const TAB_ICONS: Readonly<Record<Tab, string>> = {
  recent: '🕒',
  'smileys-emotion': '😀',
  'people-body': '👋',
  'animals-nature': '🐻',
  'food-drink': '🍔',
  'travel-places': '🚗',
  activities: '⚽',
  objects: '💡',
  symbols: '🔣',
  flags: '🏁',
};

const TONES = [0, 1, 2, 3, 4, 5] as const;
const TONE_SAMPLES = ['✋', '✋🏻', '✋🏼', '✋🏽', '✋🏾', '✋🏿'] as const;

interface Cell {
  readonly emoji: string;
  readonly label: string;
}

/**
 * The picker itself: a search box, the skin tone, the groups as tabs and one group (or the search results) as a
 * grid. Arrow keys, Home, End, Page Up and Page Down move in the grid, which is one stop in the tab order; Enter
 * in the search box picks the first result.
 */
export function PickerPanel({
  data,
  onPick,
  onClose,
  autoFocus,
}: {
  data: EmojiData;
  onPick: (emoji: string) => void;
  onClose: (() => void) | undefined;
  autoFocus: boolean;
}) {
  const t = useTranslate(messages);
  const id = useId();
  const searchRef = useRef<HTMLInputElement>(null);
  const tabsRef = useRef<HTMLDivElement>(null);
  const gridRef = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState('');
  const [tone, setTone] = useState<SkinTone>(readSkinTone);
  const [recent, setRecent] = useState(readRecent);
  const [active, setActive] = useState(0);

  const groups = useMemo(
    () => (supportsFlagEmoji() ? data.groups : data.groups.filter((group) => group.key !== 'flags')),
    [data],
  );
  // What was picked is shown as it was picked (with its skin tone); an emoji the data does not know is skipped.
  const recentCells = useMemo(
    () =>
      recent.flatMap((emoji): Cell[] => {
        const known = data.byEmoji.get(emoji);
        return known ? [{ emoji, label: known.label }] : [];
      }),
    [recent, data],
  );
  const [chosenTab, setChosenTab] = useState<Tab>(() =>
    recentCells.length > 0 ? 'recent' : 'smileys-emotion',
  );
  const tabs: Tab[] = [
    ...(recentCells.length > 0 ? (['recent'] as const) : []),
    ...groups.map((group) => group.key),
  ];
  const tab = tabs.includes(chosenTab) ? chosenTab : 'smileys-emotion';

  const searching = query.trim() !== '';
  const cells = useMemo(() => {
    const toCell = (emoji: Emoji): Cell => ({
      emoji: (tone > 0 ? emoji.skins?.[tone - 1] : undefined) ?? emoji.emoji,
      label: emoji.label,
    });
    if (searching) return searchEmojis(groups, query).map(toCell);
    if (tab === 'recent') return recentCells;
    return (groups.find((group) => group.key === tab)?.emojis ?? []).map(toCell);
  }, [searching, query, tab, tone, groups, recentCells]);
  const rows = useMemo(() => {
    const result: Cell[][] = [];
    for (let start = 0; start < cells.length; start += COLUMNS)
      result.push(cells.slice(start, start + COLUMNS));
    return result;
  }, [cells]);
  const last = cells.length - 1;
  const current = Math.max(0, Math.min(active, last));

  useEffect(() => {
    if (autoFocus) searchRef.current?.focus();
    // Only when the picker opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const focusCell = (index: number) => {
    setActive(index);
    gridRef.current?.querySelector<HTMLElement>(`[data-index="${String(index)}"]`)?.focus();
  };

  const pick = (cell: Cell) => {
    setRecent(addRecent(cell.emoji));
    onPick(cell.emoji);
  };

  const selectTab = (next: Tab) => {
    setChosenTab(next);
    setQuery('');
    setActive(0);
  };

  const searchKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      // Never submits a form the picker sits in.
      event.preventDefault();
      const first = cells[0];
      if (searching && first) pick(first);
    } else if (event.key === 'ArrowDown' && last >= 0) {
      event.preventDefault();
      focusCell(current);
    } else if (event.key === 'Escape' && query !== '') {
      event.stopPropagation();
      setQuery('');
      setActive(0);
    }
  };

  const tabsKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const at = tabs.indexOf(tab);
    const next =
      event.key === 'ArrowRight'
        ? tabs[(at + 1) % tabs.length]
        : event.key === 'ArrowLeft'
          ? tabs[(at - 1 + tabs.length) % tabs.length]
          : event.key === 'Home'
            ? tabs[0]
            : event.key === 'End'
              ? tabs[tabs.length - 1]
              : undefined;
    if (!next) return;
    event.preventDefault();
    selectTab(next);
    tabsRef.current?.querySelector<HTMLElement>(`[data-tab="${next}"]`)?.focus();
  };

  const gridKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (last < 0) return;
    const column = current % COLUMNS;
    let next = current;
    switch (event.key) {
      case 'ArrowRight':
        next = Math.min(current + 1, last);
        break;
      case 'ArrowLeft':
        next = Math.max(current - 1, 0);
        break;
      case 'ArrowDown':
        // From the row above a shorter last row, down goes to the last emoji.
        if (current + COLUMNS <= last) next = current + COLUMNS;
        else if (current - column + COLUMNS <= last) next = last;
        break;
      case 'ArrowUp':
        if (current < COLUMNS) {
          event.preventDefault();
          searchRef.current?.focus();
          return;
        }
        next = current - COLUMNS;
        break;
      case 'Home':
        next = event.ctrlKey ? 0 : current - column;
        break;
      case 'End':
        next = event.ctrlKey ? last : Math.min(current - column + COLUMNS - 1, last);
        break;
      case 'PageDown':
        for (let row = 0; row < PAGE_ROWS && next + COLUMNS <= last; row++) next += COLUMNS;
        break;
      case 'PageUp':
        next = Math.max(current - PAGE_ROWS * COLUMNS, column);
        break;
      default:
        return;
    }
    event.preventDefault();
    focusCell(next);
  };

  // Keeps the one tab stop of the grid on the emoji that was focused last, also after a click.
  const gridFocus = (event: FocusEvent<HTMLDivElement>) => {
    const index = Number(event.target.dataset.index);
    if (Number.isInteger(index)) setActive(index);
  };

  const keyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape' && onClose) {
      event.stopPropagation();
      onClose();
    }
  };

  const title = searching ? t('searchResults') : t(tab);
  return (
    <div className="be-picker" role="group" aria-label={t('picker')} onKeyDown={keyDown}>
      <div className="be-picker__top">
        <input
          ref={searchRef}
          className="ph-input be-picker__search"
          type="search"
          aria-label={t('search')}
          placeholder={t('search')}
          value={query}
          maxLength={MAX_QUERY}
          autoComplete="off"
          spellCheck={false}
          onChange={(event) => {
            setQuery(event.target.value);
            setActive(0);
          }}
          onKeyDown={searchKeyDown}
        />
        <select
          className="ph-input be-picker__tone"
          aria-label={t('skinTone')}
          title={t('skinTone')}
          value={tone}
          onChange={(event) => {
            const next = TONES.find((item) => String(item) === event.target.value) ?? 0;
            setTone(next);
            writeSkinTone(next);
          }}
        >
          {TONES.map((item) => (
            <option key={item} value={item}>
              {`${TONE_SAMPLES[item]} ${t(`tone${String(item)}` as `tone${SkinTone}`)}`}
            </option>
          ))}
        </select>
      </div>
      <div
        ref={tabsRef}
        className="be-picker__tabs"
        role="tablist"
        aria-label={t('categories')}
        onKeyDown={tabsKeyDown}
      >
        {tabs.map((key) => (
          <button
            key={key}
            type="button"
            role="tab"
            className="be-picker__tab"
            data-tab={key}
            aria-selected={!searching && key === tab}
            aria-controls={`${id}-panel`}
            aria-label={t(key)}
            title={t(key)}
            tabIndex={key === tab ? 0 : -1}
            onClick={() => {
              selectTab(key);
            }}
          >
            {TAB_ICONS[key]}
          </button>
        ))}
      </div>
      <div id={`${id}-panel`} className="be-picker__panel" role="tabpanel" aria-label={title}>
        {rows.length > 0 && (
          <div
            // A new group starts at its top.
            key={searching ? 'search' : tab}
            ref={gridRef}
            className="be-picker__grid"
            role="grid"
            aria-label={title}
            aria-rowcount={rows.length}
            aria-colcount={COLUMNS}
            onKeyDown={gridKeyDown}
            onFocus={gridFocus}
          >
            {rows.map((row, rowIndex) => (
              <div key={rowIndex} className="be-picker__row" role="row" aria-rowindex={rowIndex + 1}>
                {row.map((cell, columnIndex) => {
                  const index = rowIndex * COLUMNS + columnIndex;
                  return (
                    <div key={cell.emoji} role="gridcell" aria-colindex={columnIndex + 1}>
                      <button
                        type="button"
                        className="be-picker__emoji"
                        data-index={index}
                        aria-label={cell.label}
                        title={cell.label}
                        tabIndex={index === current ? 0 : -1}
                        onClick={() => {
                          pick(cell);
                        }}
                      >
                        {cell.emoji}
                      </button>
                    </div>
                  );
                })}
              </div>
            ))}
          </div>
        )}
        <p role="status" className={searching && last < 0 ? 'be-picker__empty' : 'ph-visually-hidden'}>
          {searching ? (last < 0 ? t('noResults') : t('resultCount', { count: cells.length })) : ''}
        </p>
      </div>
    </div>
  );
}
