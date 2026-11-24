import { memo, useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { formatMessageTime, type Locale } from '@backroom/i18n';
import { Avatar } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { Message } from './api';
import { messages } from './messages';
import type { PendingMessage } from './pending-messages';

export type Row =
  | { key: string; message: Message; pending?: undefined }
  | { key: string; message?: undefined; pending: PendingMessage };

/** Rows are about this tall until measured. */
const ESTIMATED_ROW_HEIGHT = 64;
/** Closer than this to the bottom, the list keeps following new messages. */
const FOLLOW_DISTANCE = 80;
/** Earlier messages are asked for once the oldest drawn row is this close to the top of the list. */
const LOAD_EARLIER_WITHIN = 3;

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
  locale,
  onRetry,
  onDiscard,
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
  locale: Locale;
  onRetry: (clientMessageId: string) => void;
  onDiscard: (clientMessageId: string) => void;
}) {
  const t = useTranslate(messages);
  const scrollRef = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  // Loading earlier messages waits until the list has been scrolled to its end once.
  const armed = useRef(false);
  // The row at the top of the view after the last change, and where it was: rows added or dropped above it move it.
  const previous = useRef<{ rows: readonly Row[]; anchor?: { key: string; start: number } }>({ rows });

  // The virtualizer is used only in this component and its functions are not passed to memoized children.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ESTIMATED_ROW_HEIGHT,
    getItemKey: (index) => rows[index]?.key ?? index,
    overscan: 8,
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
      role="log"
      // The list is virtualized and long: a separate live region announces new messages.
      aria-live="off"
      aria-label={t('messagesLabel', { name: channelName })}
      aria-busy={loadingEarlier || loadingNewer}
      tabIndex={0}
      onScroll={(event) => {
        const element = event.currentTarget;
        previous.current = { rows, ...anchorAt(element.scrollTop) };
        following.current = element.scrollHeight - element.scrollTop - element.clientHeight < FOLLOW_DISTANCE;
        if (element.scrollTop > 0 || following.current) armed.current = true;
      }}
    >
      {loadingEarlier && <p className="bm-log__status">{t('loadingEarlier')}</p>}
      <ol className="bm-rows" style={{ height: total }}>
        {items.map((item) => {
          const row = rows[item.index];
          if (!row) return null;
          return (
            <li
              key={item.key}
              ref={virtualizer.measureElement}
              data-index={item.index}
              className="bm-row"
              style={{ transform: `translateY(${String(item.start)}px)` }}
            >
              <MessageRow
                row={row}
                authorName={authorName}
                ownAccountId={ownAccountId}
                locale={locale}
                onRetry={onRetry}
                onDiscard={onDiscard}
              />
            </li>
          );
        })}
      </ol>
    </div>
  );
}

const MessageRow = memo(function MessageRow({
  row,
  authorName,
  ownAccountId,
  locale,
  onRetry,
  onDiscard,
}: {
  row: Row;
  authorName: (accountId: string) => string;
  ownAccountId: string | undefined;
  locale: Locale;
  onRetry: (clientMessageId: string) => void;
  onDiscard: (clientMessageId: string) => void;
}) {
  const now = new Date();
  const t = useTranslate(messages);
  const pending = row.pending;
  const message = row.message;
  const mine = pending !== undefined || message?.authorAccountId === ownAccountId;
  const name = mine ? t('you') : authorName(message?.authorAccountId ?? '');
  const createdAt = pending?.createdAt ?? message?.createdAt ?? '';
  const text = pending?.text ?? message?.text ?? '';
  return (
    <div className={pending?.status === 'failed' ? 'bm-message bm-message--failed' : 'bm-message'}>
      <Avatar name={name} />
      <div className="bm-message__body">
        <p className="bm-message__head">
          <b>{name}</b>{' '}
          <time className="ph-muted" dateTime={createdAt}>
            {formatMessageTime(new Date(createdAt), locale, now)}
          </time>
        </p>
        {/* Plain text, never markup: React escapes it; line breaks are kept by the style. */}
        <p className={pending?.status === 'sending' ? 'bm-text bm-text--sending' : 'bm-text'}>{text}</p>
        {pending?.status === 'sending' && <p className="bm-message__state ph-muted">{t('sending')}</p>}
        {pending?.status === 'failed' && (
          <p className="bm-message__state bm-message__error">
            {pending.code === 'messaging.message_invalid'
              ? t('messageInvalid')
              : pending.code === 'messaging.channel_not_found'
                ? t('channelGone')
                : t('sendFailed')}{' '}
            {pending.code !== 'messaging.message_invalid' && (
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
