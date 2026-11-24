import { useEffect, useRef, useState } from 'react';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';
import type { Row } from './message-list';

/** New messages are announced at most once in this time. */
export const ANNOUNCE_EVERY_MS = 1500;
/** An announcement carries at most this many messages (the newest) of at most this many characters each. */
const MAX_ANNOUNCED = 3;
const MAX_ANNOUNCED_CHARACTERS = 200;

/**
 * A small polite live region that reads out messages that arrive while the channel is open. The list itself is not
 * live (it is virtualized and holds hundreds of rows), so nothing else is announced. Own messages, messages loaded
 * from history and the messages present on opening are never announced.
 */
export function NewMessageAnnouncer({
  rows,
  ownAccountId,
  authorName,
  active,
}: {
  rows: readonly Row[];
  ownAccountId: string | undefined;
  authorName: (accountId: string) => string;
  /** False while reading history (newer pages are being loaded in): everything is then marked as seen. */
  active: boolean;
}) {
  const t = useTranslate(messages);
  const [text, setText] = useState('');
  const seen = useRef(-1);
  const queue = useRef<string[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const latest = useRef({ t, authorName });
  useEffect(() => {
    latest.current = { t, authorName };
  });

  useEffect(() => {
    const first = seen.current < 0;
    let newest = Math.max(seen.current, 0);
    const fresh: string[] = [];
    for (const row of rows) {
      const message = row.message;
      if (!message || message.seq <= seen.current) continue;
      newest = Math.max(newest, message.seq);
      // The rows present on opening, history and one's own messages are not announced.
      if (!first && active && message.authorAccountId !== ownAccountId) {
        fresh.push(
          latest.current.t('announceMessage', {
            name: latest.current.authorName(message.authorAccountId),
            text: message.text.slice(0, MAX_ANNOUNCED_CHARACTERS),
          }),
        );
      }
    }

    seen.current = newest;
    if (fresh.length === 0) return;
    queue.current.push(...fresh);
    timer.current ??= setTimeout(() => {
      timer.current = undefined;
      setText(queue.current.slice(-MAX_ANNOUNCED).join(' '));
      queue.current = [];
    }, ANNOUNCE_EVERY_MS);
  }, [rows, ownAccountId, active]);

  useEffect(
    () => () => {
      clearTimeout(timer.current);
    },
    [],
  );

  return (
    <div className="bm-sr-only" role="status" aria-live="polite" aria-atomic="true">
      {text}
    </div>
  );
}
