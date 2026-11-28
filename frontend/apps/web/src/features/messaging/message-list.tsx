import {
  Suspense,
  lazy,
  memo,
  useCallback,
  useEffect,
  useId,
  useImperativeHandle,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type FocusEvent,
  type KeyboardEvent,
  type Ref,
} from 'react';
import { defaultRangeExtractor, useVirtualizer, type Range } from '@tanstack/react-virtual';
import { formatMessageDay, formatMessageTime, type Locale } from '@backroom/i18n';
import { Avatar, Icon, MenuButton, SoonButton, type IconName, type MenuEntry } from '@backroom/ui';
import { config } from '@/config';
import { useTranslate } from '@/shared/locale';
import type { Message } from './api';
import { messages } from './messages';
import type { PendingMessage } from './pending-messages';
import { isRefusedContent, refusalKey } from './send-errors';
import { QuoteLine } from './message-quote';
import { MessageBody } from './rich-text/message-blocks';
import { editableBlocks, type Block } from './rich-text/blocks';

/** The editor and Lexical load as their own chunk, only when a message is edited. */
const MessageEditor = lazy(() =>
  import('./message-editor').then(({ MessageEditor: editor }) => ({ default: editor })),
);

/** What the list can do with the person's own messages (edit and delete, Phase 2 step 9); absent: nothing. */
export type MessageEditing = {
  start: (messageId: string) => void;
  cancel: () => void;
  /** Saves the new content; answers an error text to show, or nothing when it went through. */
  save: (messageId: string, blocks: Block[]) => Promise<string | undefined>;
  /** Asks for confirmation, then deletes the message. */
  remove: (messageId: string) => void;
  /**
   * True once for the editor the person just started: it takes the keyboard focus. An editor that is drawn again
   * (its row scrolled out and back) must not take it.
   */
  takeFocus: (messageId: string) => boolean;
};

/** What the owner of the list can ask of it. */
export type MessageListHandle = {
  /**
   * Puts the focus on the row with this key, scrolling to it if it is not drawn. A key that is not in the list puts
   * it on the list's own tab stop instead.
   */
  focusRow: (key: string) => void;
  /** The rows are about to become the newest messages again: the list then follows them from the end. */
  followLatest: () => void;
};

export type Row =
  | {
      key: string;
      message: Message;
      pending?: undefined;
      /** Confirmed but not in a fetched page yet (the pending overlay): it cannot be edited or deleted yet. */
      overlay?: boolean;
    }
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
/** How long the original of a quote stays highlighted after a jump to it. */
const FLASH_MS = 1600;

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
  mentionName,
  ownAccountId,
  ownName,
  locale,
  onRetry,
  onDiscard,
  onJoin,
  editing,
  editingId,
  onReply,
  detached = false,
  onJumpToLatest,
  onJumpUnloaded,
  ref,
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
  /** The name of a mentioned member if the client knows it. */
  mentionName?: ((accountId: string) => string | undefined) | undefined;
  ownAccountId: string | undefined;
  /** The signed-in account's name, shown on its own messages. */
  ownName: string;
  locale: Locale;
  onRetry: (clientMessageId: string) => void;
  onDiscard: (clientMessageId: string) => void;
  /** Joins the channel and then sends the failed message again. */
  onJoin: (clientMessageId: string) => void;
  editing?: MessageEditing | undefined;
  /** The message being edited in place, if any; kept apart from `editing` so the rows stay memoized. */
  editingId?: string | undefined;
  /** Replies to a message (the action bar's button); absent without replying: no button. */
  onReply?: ((message: Message) => void) | undefined;
  /**
   * The rows are a window in the history, not the newest messages (after a jump, or after reading far back): the list
   * does not follow new messages, and offers to go back to the newest ones (`onJumpToLatest`).
   */
  detached?: boolean | undefined;
  onJumpToLatest?: (() => void) | undefined;
  /**
   * Goes to a quoted original that is not in the rows (the owner replaces them with a window around it); the list
   * scrolls to it, marks it and moves the focus as for a loaded one once it is in the rows. Absent: such a quote is text.
   */
  onJumpUnloaded?: ((messageId: string) => Promise<boolean>) | undefined;
  ref?: Ref<MessageListHandle>;
}) {
  const t = useTranslate(messages);
  const scrollRef = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  // Loading earlier messages waits until the list has been scrolled to its end once.
  const armed = useRef(false);
  // The row a jump scrolls to, until the drawn rows include it or a scroll event reports where the list is: until then
  // the drawn rows may be those of the old position (the end, or the top), which must not ask for pages. No timer: on a
  // busy device the scroll lands late, and rows drawn from the old position are still wrong then.
  // A ref, so the effects of the very commit that drew the jump's rows already see it.
  const settleKey = useRef<string>(undefined);
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
  const editingIdRef = useRef(editingId);
  editingIdRef.current = editingId;
  const rangeExtractor = useCallback((range: Range) => {
    const indexes = defaultRangeExtractor(range);
    // The row that holds focus and the row being edited stay drawn, whatever the scroll position.
    const focused = focusedKey.current;
    const editing = editingIdRef.current;
    const keep = [
      focused === undefined ? -1 : rowsRef.current.findIndex((row) => row.key === focused),
      editing === undefined ? -1 : rowsRef.current.findIndex((row) => row.message?.id === editing),
    ].filter((at) => at >= 0 && !indexes.includes(at));
    return keep.length === 0 ? indexes : [...indexes, ...keep].sort((a, b) => a - b);
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
  // An edit started from elsewhere (ArrowUp in the composer) may be for a row far from the view: bring it in.
  useEffect(() => {
    if (editingId === undefined) return;
    const at = rowsRef.current.findIndex((row) => row.message?.id === editingId);
    if (at < 0) return;
    // Going to the row is a move away from the end: the list must not snap back to following new messages.
    following.current = false;
    virtualizer.scrollToIndex(at);
  }, [editingId, virtualizer]);

  /** The row at the top of the view, as the anchor to keep still when rows are added or dropped above it. */
  const anchorAt = useCallback(
    (scrollTop: number) => {
      const visible = virtualizer.measurementsCache.find((item) => item.end > scrollTop);
      const row = visible && rows[visible.index];
      return visible && row ? { anchor: { key: row.key, start: visible.start } } : {};
    },
    [rows, virtualizer],
  );

  // A detached list never follows; once the newest messages are back after the person asked for them, it does.
  const followNext = useRef(false);
  // "Jump to latest" had the focus: it moves to the newest message, as the button goes away.
  const focusLatest = useRef(false);
  useLayoutEffect(() => {
    const element = scrollRef.current;
    if (!element) return;
    const before = previous.current;
    if (detached) {
      following.current = false;
    } else if (followNext.current) {
      followNext.current = false;
      following.current = true;
      const last = rows.at(-1);
      // Also ends a scroll to an original that is still settling, which would otherwise pull the view back to it.
      if (last) virtualizer.scrollToIndex(rows.length - 1, { align: 'end' });
      if (focusLatest.current && last) {
        focusLatest.current = false;
        const lastElement = element.querySelector<HTMLElement>(
          `[${ROW_KEY_ATTRIBUTE}="${CSS.escape(last.key)}"]`,
        );
        if (lastElement) lastElement.focus({ preventScroll: true });
        else pendingFocus.current = last.key;
      }
    }
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
  }, [rows, total, virtualizer, anchorAt, detached]);

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
  const tabStopRef = useRef(tabStopKey);
  tabStopRef.current = tabStopKey;
  useImperativeHandle(
    ref,
    () => ({
      focusRow: (key) => {
        const present = rowsRef.current.some((row) => row.key === key);
        const target = present ? key : tabStopRef.current;
        const at = target === undefined ? -1 : rowsRef.current.findIndex((row) => row.key === target);
        if (target === undefined || at < 0) return;
        const element = rowElement(target);
        if (element) {
          element.focus({ preventScroll: true });
        } else {
          // Not drawn: scroll it in; the focus follows once it is (as for the arrow keys).
          virtualizer.scrollToIndex(at);
          pendingFocus.current = target;
        }
      },
      followLatest: () => {
        followNext.current = true;
      },
    }),
    [virtualizer],
  );
  // The messages that are in the list: a quote can go to its original when it is one of them, or when the owner can
  // bring it in.
  const canJumpUnloaded = onJumpUnloaded !== undefined;
  const loadedIds = useMemo(
    () => new Set(rows.flatMap((row) => (row.message ? [row.message.id] : []))),
    [rows],
  );
  const [flashId, setFlashId] = useState<string>();
  const flashTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(
    () => () => {
      clearTimeout(flashTimer.current);
    },
    [],
  );
  const onJumpUnloadedRef = useRef(onJumpUnloaded);
  onJumpUnloadedRef.current = onJumpUnloaded;
  /** The original asked for that is not in the rows yet; the latest request wins. */
  const pendingJump = useRef<{ messageId: string }>(undefined);
  /** Goes to the original of a quote and marks it for a moment; the keyboard's focus follows. */
  const jump = useCallback(
    (messageId: string, keyboard: boolean) => {
      const at = rowsRef.current.findIndex((row) => row.message?.id === messageId);
      const key = rowsRef.current[at]?.key;
      pendingJump.current = undefined;
      if (key === undefined) {
        const load = onJumpUnloadedRef.current;
        if (!load) return;
        // The rows are replaced by a window around the original, so the quote that was used goes away with them:
        // the focus moves to the original however the quote was used.
        const request = { messageId };
        pendingJump.current = request;
        void load(messageId).then((shown) => {
          if (!shown && pendingJump.current === request) pendingJump.current = undefined;
        });
        return;
      }
      // Going to the row is a move away from the end: the list must not snap back to following new messages.
      following.current = false;
      virtualizer.scrollToIndex(at, { align: 'center' });
      settleKey.current = key;
      clearTimeout(flashTimer.current);
      setFlashId(messageId);
      flashTimer.current = setTimeout(() => {
        setFlashId(undefined);
      }, FLASH_MS);
      if (!keyboard) return;
      setActiveKey(key);
      focusedIndex.current = at;
      const element = rowElement(key);
      if (element) element.focus({ preventScroll: true });
      else pendingFocus.current = key;
    },
    [virtualizer],
  );
  // The window around an original asked for has arrived: go to it as to a loaded one, with the focus.
  useLayoutEffect(() => {
    const request = pendingJump.current;
    if (!request || !rows.some((row) => row.message?.id === request.messageId)) return;
    // The row that had the focus (the reply whose quote was used) is gone with the old rows: the focus goes to the
    // original, not to the row now nearest to where that one was.
    focusedKey.current = undefined;
    jump(request.messageId, true);
  }, [rows, jump]);
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
  /** Pages may load from the drawn rows only once they are those around the jump's row (or no jump is pending). */
  const landed = useCallback(() => {
    const key = settleKey.current;
    const at = key === undefined ? -1 : rowsRef.current.findIndex((row) => row.key === key);
    if (at < 0) return true;
    if (firstIndex === undefined || lastIndex === undefined || at < firstIndex || at > lastIndex)
      return false;
    settleKey.current = undefined;
    return true;
  }, [firstIndex, lastIndex]);
  useEffect(() => {
    if (
      armed.current &&
      hasEarlier &&
      !loadingEarlier &&
      firstIndex !== undefined &&
      firstIndex <= LOAD_EARLIER_WITHIN &&
      landed()
    ) {
      onLoadEarlier();
    }
  }, [firstIndex, hasEarlier, loadingEarlier, onLoadEarlier, landed]);
  useEffect(() => {
    if (
      armed.current &&
      hasNewer &&
      !loadingNewer &&
      lastIndex !== undefined &&
      lastIndex >= rows.length - 1 - LOAD_EARLIER_WITHIN &&
      landed()
    ) {
      onLoadNewer();
    }
  }, [lastIndex, rows.length, hasNewer, loadingNewer, onLoadNewer, landed]);

  return (
    <>
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
          // A scroll event says where the list really is: the drawn rows are right from now on.
          settleKey.current = undefined;
          previous.current = { rows, ...anchorAt(element.scrollTop) };
          following.current =
            !detached && element.scrollHeight - element.scrollTop - element.clientHeight < FOLLOW_DISTANCE;
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
            const dayLabel =
              day.key === today ? t('today') : day.key === yesterday ? t('yesterday') : day.text;
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
                  mentionName={mentionName}
                  ownAccountId={ownAccountId}
                  ownName={ownName}
                  locale={locale}
                  onRetry={onRetry}
                  onDiscard={onDiscard}
                  onJoin={onJoin}
                  editing={editing}
                  isEditing={editingId !== undefined && row.message?.id === editingId}
                  onReply={onReply}
                  onJump={jump}
                  quoteReachable={
                    row.message?.replyTo !== undefined &&
                    row.message.replyTo !== null &&
                    (canJumpUnloaded || loadedIds.has(row.message.replyTo.messageId))
                  }
                  flash={flashId !== undefined && row.message?.id === flashId}
                />
              </li>
            );
          })}
        </ol>
      </div>
      {detached && onJumpToLatest && (
        // After the log in the page, so Tab reaches it between the messages and the composer; it floats over the
        // log's foot. Its focus goes to the newest message once it has done its work (and is gone).
        <div className="bm-latest">
          <button
            type="button"
            className="bm-latest__button"
            onClick={(event) => {
              const hadFocus = event.currentTarget === document.activeElement;
              followNext.current = true;
              focusLatest.current = hadFocus;
              onJumpToLatest();
            }}
          >
            <Icon name="chevronDown" size={16} />
            {t('jumpToLatest')}
          </button>
        </div>
      )}
    </>
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
 * Shown over the message under the pointer, with focus inside or tapped; the actions of later phases are passive. It
 * is built only for that one row, so a long list does not carry six buttons per message. Tab from the row reaches the
 * buttons and Escape returns to the row. On the person's own message the "more" button opens a menu with edit and
 * delete (the message is parsed for editing only now, for this one row).
 */
function MessageActions({
  message,
  editing,
  onReply,
}: {
  message: Message | undefined;
  editing: MessageEditing | undefined;
  /** Replies to this message: the reply button works. */
  onReply: (() => void) | undefined;
}) {
  const t = useTranslate(messages);
  const entries: MenuEntry[] = [];
  if (editing && message) {
    if (editableBlocks(message.blocks, message.text) !== null) {
      entries.push({
        kind: 'item',
        id: 'edit',
        label: t('editMessage'),
        onSelect: () => {
          editing.start(message.id);
        },
      });
    }

    entries.push({
      kind: 'item',
      id: 'delete',
      label: t('deleteMessage'),
      danger: true,
      onSelect: () => {
        editing.remove(message.id);
      },
    });
  }

  return (
    <div className="bm-actions">
      {MESSAGE_ACTIONS.map((action) =>
        action.key === 'more' && entries.length > 0 ? (
          <MenuButton
            key={action.key}
            label={t(action.key)}
            button={<Icon name={action.icon} size={16} />}
            menuClassName="bm-actions__menu"
            placement="auto"
            entries={entries}
          />
        ) : action.key === 'reply' && onReply ? (
          <button key={action.key} type="button" aria-label={t('reply')} title={t('reply')} onClick={onReply}>
            <Icon name={action.icon} size={16} />
          </button>
        ) : (
          <SoonButton key={action.key} label={t(action.key)} soon={t('soon')}>
            <Icon name={action.icon} size={16} />
          </SoonButton>
        ),
      )}
    </div>
  );
}

const MessageRow = memo(function MessageRow({
  row,
  tabStop,
  position,
  authorName,
  mentionName,
  ownAccountId,
  ownName,
  locale,
  onRetry,
  onDiscard,
  onJoin,
  editing,
  isEditing,
  onReply,
  onJump,
  quoteReachable,
  flash,
}: {
  row: Row;
  /** The list's one tab stop (roving tabindex); the other rows are reached by the arrow keys. */
  tabStop: boolean;
  /** One-based place in the feed (aria-posinset); the size is unknown, as more pages load. */
  position: number;
  authorName: (accountId: string) => string;
  /** The name of a mentioned member if the client knows it. */
  mentionName?: ((accountId: string) => string | undefined) | undefined;
  ownAccountId: string | undefined;
  ownName: string;
  locale: Locale;
  onRetry: (clientMessageId: string) => void;
  onDiscard: (clientMessageId: string) => void;
  onJoin: (clientMessageId: string) => void;
  editing: MessageEditing | undefined;
  /** This row's message is the one being edited. */
  isEditing: boolean;
  onReply: ((message: Message) => void) | undefined;
  onJump: (messageId: string, keyboard: boolean) => void;
  /** The original of this row's quote is in the list, or can be brought into it. */
  quoteReachable: boolean;
  /** This row is the original the person just jumped to. */
  flash: boolean;
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
  const deleted = Boolean(message?.deletedAt);
  const editingMessage = editing && message && isEditing ? message : undefined;
  const editingThis = editingMessage !== undefined;
  const quote = pending?.replyTo ?? message?.replyTo ?? undefined;
  const quoteAuthor = quote?.authorAccountId ?? '';
  // Confirmed and not deleted: any message can be replied to (an unsent one has no id yet).
  const replyTarget = onReply && message && !deleted ? message : undefined;
  // Own, confirmed and not deleted: the only messages that can be edited or deleted here.
  const manageable =
    editing !== undefined &&
    message !== undefined &&
    !row.overlay &&
    !deleted &&
    ownAccountId !== undefined &&
    message.authorAccountId === ownAccountId;
  // The row takes the focus back when its editor closes.
  const wasEditing = useRef(false);
  useEffect(() => {
    if (wasEditing.current && !editingThis) {
      // The editor that had the focus is gone: the row takes it, unless the person has moved on.
      const active = document.activeElement;
      if (active === null || active === document.body || rowRef.current?.contains(active)) {
        rowRef.current?.focus({ preventScroll: true });
      }
    }
    wasEditing.current = editingThis;
  }, [editingThis]);
  // Focus can leave the row with no blur: a focused element inside it that is removed (the item of a menu closed by a
  // press elsewhere, the bar's button when an edit starts) drops the focus to the page, and the row never hears of
  // it. While focus or a tap keeps the bar shown, the next focus move, press or key outside the row checks that the
  // focus is still inside, so a bar cannot stay behind on a row the person has left. Only such a row listens.
  const pinned = focused || tapped;
  useEffect(() => {
    if (!pinned) return;
    const check = (event: Event) => {
      const element = rowRef.current;
      if (!element || (event.target instanceof Node && element.contains(event.target))) return;
      if (element.contains(document.activeElement)) return;
      setFocused(false);
      setTapped(false);
    };
    const options = { capture: true, passive: true };
    document.addEventListener('focusin', check, options);
    document.addEventListener('pointerup', check, options);
    document.addEventListener('keyup', check, options);
    return () => {
      document.removeEventListener('focusin', check, options);
      document.removeEventListener('pointerup', check, options);
      document.removeEventListener('keyup', check, options);
    };
  }, [pinned]);
  if (message && deleted) {
    // A tombstone (ADR 0011): its place stays, with no author, avatar or actions.
    return (
      <div
        ref={rowRef}
        className={
          flash ? 'bm-message bm-message--deleted bm-message--flash' : 'bm-message bm-message--deleted'
        }
        role="article"
        aria-labelledby={headId}
        aria-posinset={position}
        aria-setsize={-1}
        data-row-key={row.key}
        tabIndex={tabStop ? 0 : -1}
      >
        <p className="bm-tombstone" id={headId}>
          {t('messageDeleted')}
        </p>
      </div>
    );
  }

  return (
    <div
      ref={rowRef}
      className={
        (pending?.status === 'failed' ? 'bm-message bm-message--failed' : 'bm-message') +
        (flash ? ' bm-message--flash' : '')
      }
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
        // The open menu closes itself first (and returns to its button); the next Escape returns to the row.
        if (
          (event.target as HTMLElement).closest('.bm-actions') &&
          !(event.target as HTMLElement).closest('[role="menu"]')
        ) {
          event.preventDefault();
          event.stopPropagation();
          rowRef.current?.focus({ preventScroll: true });
        }
      }}
    >
      {(hovered || focused || tapped) && !pending && !editingThis && (
        <MessageActions
          message={manageable ? message : undefined}
          editing={manageable ? editing : undefined}
          onReply={
            replyTarget && onReply
              ? () => {
                  onReply(replyTarget);
                }
              : undefined
          }
        />
      )}
      <Avatar name={name} toneKey={authorId} shape="square" size="lg" />
      <div className="bm-message__body">
        {quote && (
          <QuoteLine
            quote={quote}
            authorId={quoteAuthor}
            name={quoteAuthor !== '' && quoteAuthor === ownAccountId ? ownName : authorName(quoteAuthor)}
            canJump={quoteReachable}
            onJump={onJump}
          />
        )}
        <p className="bm-message__head" id={headId}>
          <b>{name}</b>{' '}
          <time className="ph-muted" dateTime={createdAt}>
            {formatMessageTime(new Date(createdAt), locale, now)}
          </time>
          {message?.editedAt && <span className="ph-muted"> · {t('edited')}</span>}
        </p>
        {/* Plain text, never markup: React escapes it; line breaks are kept by the style. */}
        {editing && editingMessage ? (
          <Suspense fallback={<p className="bm-text bm-text--sending">{text}</p>}>
            <MessageEditor
              initial={editableBlocks(editingMessage.blocks, text) ?? []}
              onSave={(blocks) => editing.save(editingMessage.id, blocks)}
              onCancel={editing.cancel}
              takeFocus={() => editing.takeFocus(editingMessage.id)}
            />
          </Suspense>
        ) : config.richTextEnabled ? (
          <MessageBody
            blocks={pending?.blocks ?? message?.blocks}
            text={text}
            sending={pending?.status === 'sending'}
            {...(mentionName ? { mentionName } : {})}
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
                  : pending.code === 'messaging.conversation_archived'
                    ? t('sendArchived')
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
