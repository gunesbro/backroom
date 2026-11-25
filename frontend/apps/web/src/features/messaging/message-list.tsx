import {
  memo,
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type FocusEvent,
  type KeyboardEvent,
} from 'react';
import { defaultRangeExtractor, useVirtualizer, type Range } from '@tanstack/react-virtual';
import { formatMessageDay, formatMessageTime, type Locale } from '@backroom/i18n';
import { Avatar, Icon, SoonButton, type IconName } from '@backroom/ui';
import { config } from '@/config';
import { useTranslate } from '@/shared/locale';
import type { Message } from './api';
import { messages } from './messages';
import type { PendingMessage } from './pending-messages';
import { isRefusedContent, refusalKey } from './send-errors';
import { MessageBody } from './rich-text/message-blocks';

export type Row =
  | { key: string; message: Message; pending?: undefined }
  | { key: string; message?: undefined; pending: PendingMessage };

const DAY_MS = 24 * 60 * 60 * 1000;
/** Message times whose day is remembered; the list keeps a few hundred messages at most. */
const MAX_CACHED_DAYS = 2000;
/** Rows are about this tall until measured. */
const ESTIMATED_ROW_HEIGHT = 64;
/** Closer than this to the bottom, the list keeps following new messages. */
const FOLLOW_DISTANCE = 80;
/** Earlier messages are asked for once the oldest drawn row is this close to the top of the list. */
const LOAD_EARLIER_WITHIN = 3;
const ROW_KEY_ATTRIBUTE = 'data-row-key';

/**
 * The messages of a channel, oldest first, virtualized (TanStack Virtual): only the rows near the view are in the
 * page. It is a log, so a screen reader announces rows as they are added; the keyboard scrolls it.
 */
export function MessageList({
  channelName,
  rows,
  hasEarlier,
  loadingEarlier,
  onLoadEarlier,
  hasNewer,
  loadingNewer,
  onLoadNewer,
  authorName,
  ownAccountId,
  ownName,
  locale,
  onRetry,
  onDiscard,
  onJoin,
}: {
  channelName: string;
  rows: readonly Row[];
  hasEarlier: boolean;
  loadingEarlier: boolean;
  onLoadEarlier: () => void;
  /** Pages of newer messages were dropped from memory and are loaded again near the end of the list. */
  hasNewer: boolean;
  loadingNewer: boolean;
  onLoadNewer: () => void;
  authorName: (accountId: string) => string;
  ownAccountId: string | undefined;
  /** The signed-in account's name, shown on its own messages. */
  ownName: string;
  locale: Locale;
  onRetry: (clientMessageId: string) => void;
  onDiscard: (clientMessageId: string) => void;
  /** Joins the channel and then sends the failed message again. */
  onJoin: (clientMessageId: string) => void;
}) {
  const t = useTranslate(messages);
  const scrollRef = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  // Loading earlier messages waits until the list has been scrolled to its end once.
  const armed = useRef(false);
  // The row at the top of the view after the last change, and where it was: rows added or dropped above it move it.
  const previous = useRef<{ rows: readonly Row[]; anchor?: { key: string; start: number } }>({ rows });

  // Roving tabindex: the row that is the list's one tab stop, and the row that holds focus (kept drawn while it does).
  const [activeKey, setActiveKey] = useState<string>();
  const focusedKey = useRef<string>(undefined);
  const pendingFocus = useRef<string>(undefined);
  const focusedIndex = useRef(0);
  const previousRows = useRef<readonly Row[]>(rows);
  const rowsRef = useRef(rows);
  rowsRef.current = rows;
  const rangeExtractor = useCallback((range: Range) => {
    const indexes = defaultRangeExtractor(range);
    const focused = focusedKey.current;
    const at = focused === undefined ? -1 : rowsRef.current.findIndex((row) => row.key === focused);
    if (at < 0 || indexes.includes(at)) return indexes;
    return [...indexes, at].sort((a, b) => a - b);
  }, []);

  // The virtualizer is used only in this component and its functions are not passed to memoized children.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ESTIMATED_ROW_HEIGHT,
    getItemKey: (index) => rows[index]?.key ?? index,
    overscan: 8,
    rangeExtractor,
  });
  const total = virtualizer.getTotalSize();

  /** The row at the top of the view, as the anchor to keep still when rows are added or dropped above it. */
  const anchorAt = useCallback(
    (scrollTop: number) => {
      const visible = virtualizer.measurementsCache.find((item) => item.end > scrollTop);
      const row = visible && rows[visible.index];
      return visible && row ? { anchor: { key: row.key, start: visible.start } } : {};
    },
    [rows, virtualizer],
  );

  useLayoutEffect(() => {
    const element = scrollRef.current;
    if (!element) return;
    const before = previous.current;
    if (following.current) {
      element.scrollTop = element.scrollHeight;
    } else if (before.rows !== rows && before.anchor) {
      // Earlier messages were added above, or the oldest page was dropped below the view: keep what the person is
      // reading where it was, by the move of the row at the top of the view.
      const { key, start } = before.anchor;
      const index = rows.findIndex((row) => row.key === key);
      const now = virtualizer.measurementsCache[index]?.start;
      if (now !== undefined && now !== start) element.scrollTop += now - start;
    }

    previous.current = { rows, ...anchorAt(element.scrollTop) };
    if (element.scrollHeight <= element.clientHeight) armed.current = true;
  }, [rows, total, virtualizer, anchorAt]);

  const items = virtualizer.getVirtualItems();
  // Day keys and texts are formatted once per message time and language, not on every scroll; today and yesterday
  // once per render, so the labels move on at midnight.
  const days = useRef<{ locale: Locale; byTime: Map<string, { key: string; text: string }> }>({
    locale,
    byTime: new Map(),
  });
  if (days.current.locale !== locale) days.current = { locale, byTime: new Map() };
  const now = new Date();
  const today = formatMessageDay(now, locale, now).key;
  const yesterday = formatMessageDay(new Date(now.getTime() - DAY_MS), locale, now).key;
  const dayOf = (createdAt: string) => {
    const cache = days.current.byTime;
    let day = cache.get(createdAt);
    if (!day) {
      if (cache.size >= MAX_CACHED_DAYS) cache.clear();
      const { key, text } = formatMessageDay(new Date(createdAt), locale, now);
      day = { key, text };
      cache.set(createdAt, day);
    }

    return day;
  };
  // Without a known (or drawn) active row, the last drawn one is the tab stop.
  const lastDrawn = items.at(-1);
  const tabStopKey =
    activeKey !== undefined && items.some((item) => rows[item.index]?.key === activeKey)
      ? activeKey
      : lastDrawn && rows[lastDrawn.index]?.key;
  const rowElement = (key: string) =>
    scrollRef.current?.querySelector<HTMLElement>(`[${ROW_KEY_ATTRIBUTE}="${CSS.escape(key)}"]`);
  // Moving by key scrolls the target row into the drawn range first; focus follows once it is in the page.
  useLayoutEffect(() => {
    const key = pendingFocus.current;
    const element = key === undefined ? undefined : rowElement(key);
    if (element) {
      pendingFocus.current = undefined;
      element.focus({ preventScroll: true });
    }
  }, [items]);
  const onLogFocus = (event: FocusEvent<HTMLElement>) => {
    const key = event.target.closest(`[${ROW_KEY_ATTRIBUTE}]`)?.getAttribute(ROW_KEY_ATTRIBUTE);
    focusedKey.current = key ?? undefined;
    if (key) {
      setActiveKey(key);
      const at = rows.findIndex((row) => row.key === key);
      if (at >= 0) focusedIndex.current = at;
    }
  };
  const onLogBlur = (event: FocusEvent<HTMLElement>) => {
    // A row removed while it has focus can report a blur; it is the row's loss, not the person's move.
    if (event.relatedTarget === null && !event.target.isConnected) return;
    focusedKey.current = undefined;
  };
  // The focused row left the list (its page was dropped): focus goes to the row now in its place, not to the page.
  useLayoutEffect(() => {
    const key = focusedKey.current;
    const before = previousRows.current;
    previousRows.current = rows;
    if (key === undefined) return;
    const at = rows.findIndex((row) => row.key === key);
    if (at >= 0) {
      focusedIndex.current = at;
      return;
    }

    // The nearest row that is still there: the next one after the old position, else the one before it.
    const survivors = new Set(rows.map((row) => row.key));
    const after = before.slice(focusedIndex.current + 1).find((row) => survivors.has(row.key));
    const earlier = before.slice(0, focusedIndex.current).findLast((row) => survivors.has(row.key));
    const nearestKey = (after ?? earlier)?.key;
    const nearest = rows.findIndex((row) => row.key === nearestKey);
    focusedKey.current = undefined;
    if (nearestKey === undefined || nearest < 0) return;
    focusedIndex.current = nearest;
    setActiveKey(nearestKey);
    virtualizer.scrollToIndex(nearest);
    // Drawn already: focus now (the items may not change); otherwise after the scroll draws it.
    const element = rowElement(nearestKey);
    if (element) element.focus({ preventScroll: true });
    else pendingFocus.current = nearestKey;
  }, [rows, virtualizer]);
  const onRowKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    const key = (event.target as HTMLElement).getAttribute(ROW_KEY_ATTRIBUTE);
    if (key === null || event.altKey || event.ctrlKey || event.metaKey) return;
    const index = rows.findIndex((row) => row.key === key);
    const next =
      event.key === 'ArrowUp' || event.key === 'PageUp'
        ? Math.max(index - 1, 0)
        : event.key === 'ArrowDown' || event.key === 'PageDown'
          ? Math.min(index + 1, rows.length - 1)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? rows.length - 1
              : undefined;
    const nextKey = next === undefined ? undefined : rows[next]?.key;
    if (next === undefined || nextKey === undefined) return;
    event.preventDefault();
    setActiveKey(nextKey);
    focusedIndex.current = next;
    virtualizer.scrollToIndex(next);
    // Already drawn: focus now; otherwise the effect above does it after the scroll draws the row.
    const element = rowElement(nextKey);
    if (element) element.focus({ preventScroll: true });
    else pendingFocus.current = nextKey;
  };
  const firstIndex = items[0]?.index;
  const lastIndex = items.at(-1)?.index;
  useEffect(() => {
    if (
      armed.current &&
      hasEarlier &&
      !loadingEarlier &&
      firstIndex !== undefined &&
      firstIndex <= LOAD_EARLIER_WITHIN
    ) {
      onLoadEarlier();
    }
  }, [firstIndex, hasEarlier, loadingEarlier, onLoadEarlier]);
  useEffect(() => {
    if (
      armed.current &&
      hasNewer &&
      !loadingNewer &&
      lastIndex !== undefined &&
      lastIndex >= rows.length - 1 - LOAD_EARLIER_WITHIN
    ) {
      onLoadNewer();
    }
  }, [lastIndex, rows.length, hasNewer, loadingNewer, onLoadNewer]);

  return (
    <div
      ref={scrollRef}
      className="bm-log"
      role="feed"
      // The list is virtualized and long: a separate live region announces new messages.
      aria-label={t('messagesLabel', { name: channelName })}
      aria-busy={loadingEarlier || loadingNewer}
      onKeyDown={onRowKeyDown}
      onFocus={onLogFocus}
      onBlur={onLogBlur}
      onScroll={(event) => {
        const element = event.currentTarget;
        previous.current = { rows, ...anchorAt(element.scrollTop) };
        following.current = element.scrollHeight - element.scrollTop - element.clientHeight < FOLLOW_DISTANCE;
        if (element.scrollTop > 0 || following.current) armed.current = true;
      }}
    >
      {loadingEarlier && <p className="bm-log__status">{t('loadingEarlier')}</p>}
      <ol className="bm-rows" role="none" style={{ height: total }}>
        {items.map((item) => {
          const row = rows[item.index];
          if (!row) return null;
          // A day divider above the first message of each day (docs/design: Main, "Bugün").
          const day = dayOf(createdAtOf(row));
          const previous = rows[item.index - 1];
          const newDay = !previous || dayOf(createdAtOf(previous)).key !== day.key;
          const dayLabel = day.key === today ? t('today') : day.key === yesterday ? t('yesterday') : day.text;
          return (
            <li
              key={item.key}
              role="none"
              ref={virtualizer.measureElement}
              data-index={item.index}
              className="bm-row"
              style={{ transform: `translateY(${String(item.start)}px)` }}
            >
              {newDay && (
                <p className="bm-day">
                  <span>{dayLabel}</span>
                </p>
              )}
              <MessageRow
                row={row}
                tabStop={row.key === tabStopKey}
                position={item.index + 1}
                authorName={authorName}
                ownAccountId={ownAccountId}
                ownName={ownName}
                locale={locale}
                onRetry={onRetry}
                onDiscard={onDiscard}
                onJoin={onJoin}
              />
            </li>
          );
        })}
      </ol>
    </div>
  );
}

function createdAtOf(row: Row): string {
  return row.pending?.createdAt ?? row.message?.createdAt ?? '';
}

/** The message actions of later phases (docs/design: Main, the bar over a message under the pointer). */
const MESSAGE_ACTIONS: readonly {
  key: 'addReaction' | 'reply' | 'startThread' | 'forward' | 'save' | 'more';
  icon: IconName;
}[] = [
  { key: 'addReaction', icon: 'smile' },
  { key: 'reply', icon: 'reply' },
  { key: 'startThread', icon: 'thread' },
  { key: 'forward', icon: 'forward' },
  { key: 'save', icon: 'bookmark' },
  { key: 'more', icon: 'more' },
];

/**
 * Shown over the message under the pointer, with focus inside or tapped; every action is for a later phase. It is
 * built only for that one row, so a long list does not carry six buttons per message. Tab from the row reaches the
 * buttons and Escape returns to the row.
 */
function MessageActions() {
  const t = useTranslate(messages);
  return (
    <div className="bm-actions">
      {MESSAGE_ACTIONS.map((action) => (
        <SoonButton key={action.key} label={t(action.key)} soon={t('soon')}>
          <Icon name={action.icon} size={16} />
        </SoonButton>
      ))}
    </div>
  );
}

const MessageRow = memo(function MessageRow({
  row,
  tabStop,
  position,
  authorName,
  ownAccountId,
  ownName,
  locale,
  onRetry,
  onDiscard,
  onJoin,
}: {
  row: Row;
  /** The list's one tab stop (roving tabindex); the other rows are reached by the arrow keys. */
  tabStop: boolean;
  /** One-based place in the feed (aria-posinset); the size is unknown, as more pages load. */
  position: number;
  authorName: (accountId: string) => string;
  ownAccountId: string | undefined;
  ownName: string;
  locale: Locale;
  onRetry: (clientMessageId: string) => void;
  onDiscard: (clientMessageId: string) => void;
  onJoin: (clientMessageId: string) => void;
}) {
  const now = new Date();
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const [tapped, setTapped] = useState(false);
  const rowRef = useRef<HTMLDivElement>(null);
  const headId = useId();
  // A press that focuses the row (mouse, finger) is not keyboard focus: it must not pin the action bar open.
  const pressed = useRef(false);
  const t = useTranslate(messages);
  const pending = row.pending;
  const message = row.message;
  const mine = pending !== undefined || message?.authorAccountId === ownAccountId;
  // Own messages carry the account's name, as everyone else sees them (docs/design: Thread).
  const authorId = message?.authorAccountId ?? ownAccountId ?? '';
  const name = mine ? ownName : authorName(authorId);
  const createdAt = pending?.createdAt ?? message?.createdAt ?? '';
  const text = pending?.text ?? message?.text ?? '';
  return (
    <div
      ref={rowRef}
      className={pending?.status === 'failed' ? 'bm-message bm-message--failed' : 'bm-message'}
      role="article"
      aria-labelledby={headId}
      aria-posinset={position}
      aria-setsize={-1}
      data-row-key={row.key}
      tabIndex={tabStop ? 0 : -1}
      // The action bar is built only for the row under the pointer, with focus inside, or tapped: not for every row.
      onPointerEnter={(event) => {
        if (event.pointerType === 'mouse') setHovered(true);
      }}
      onPointerLeave={() => {
        setHovered(false);
      }}
      onPointerDownCapture={() => {
        pressed.current = true;
      }}
      onFocus={() => {
        if (!pressed.current) setFocused(true);
        pressed.current = false;
      }}
      onBlur={(event) => {
        if (event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)) return;
        setFocused(false);
        setTapped(false);
      }}
      onPointerUp={(event) => {
        pressed.current = false;
        // A tap on touch or pen has no hover: it shows the bar, and a second tap on the message hides it.
        if (event.pointerType === 'mouse' || (event.target as HTMLElement).closest('.bm-actions, button, a'))
          return;
        setTapped((value) => !value);
      }}
      onKeyDownCapture={(event) => {
        if (event.key !== 'Escape') return;
        setTapped(false);
        // From inside the action bar back to the row; Escape elsewhere is left alone.
        if ((event.target as HTMLElement).closest('.bm-actions')) {
          event.stopPropagation();
          rowRef.current?.focus({ preventScroll: true });
        }
      }}
    >
      {(hovered || focused || tapped) && !pending && <MessageActions />}
      <Avatar name={name} toneKey={authorId} shape="square" size="lg" />
      <div className="bm-message__body">
        <p className="bm-message__head" id={headId}>
          <b>{name}</b>{' '}
          <time className="ph-muted" dateTime={createdAt}>
            {formatMessageTime(new Date(createdAt), locale, now)}
          </time>
        </p>
        {/* Plain text, never markup: React escapes it; line breaks are kept by the style. */}
        {config.richTextEnabled ? (
          <MessageBody
            blocks={pending?.blocks ?? message?.blocks}
            text={text}
            sending={pending?.status === 'sending'}
          />
        ) : (
          <p className={pending?.status === 'sending' ? 'bm-text bm-text--sending' : 'bm-text'}>{text}</p>
        )}
        {pending?.status === 'sending' && <p className="bm-message__state ph-muted">{t('sending')}</p>}
        {pending?.status === 'failed' && (
          <p className="bm-message__state bm-message__error">
            {isRefusedContent(pending.code)
              ? t(refusalKey(pending.code ?? '', pending.reason))
              : pending.code === 'messaging.conversation_not_found'
                ? t('channelGone')
                : pending.code === 'messaging.not_a_member'
                  ? t('notAMember')
                  : t('sendFailed')}{' '}
            {pending.code === 'messaging.not_a_member' && (
              <button
                type="button"
                className="ph-link"
                onClick={() => {
                  onJoin(pending.clientMessageId);
                }}
              >
                {t('join')}
              </button>
            )}{' '}
            {!isRefusedContent(pending.code) && (
              <button
                type="button"
                className="ph-link"
                onClick={() => {
                  onRetry(pending.clientMessageId);
                }}
              >
                {t('retry')}
              </button>
            )}{' '}
            <button
              type="button"
              className="ph-link"
              onClick={() => {
                onDiscard(pending.clientMessageId);
              }}
            >
              {t('discard')}
            </button>
          </p>
        )}
      </div>
    </div>
  );
});
