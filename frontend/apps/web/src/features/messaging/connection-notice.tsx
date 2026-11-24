import { useEffect, useState } from 'react';
import type { ConnectionStatus } from '@backroom/realtime';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';

/** A short loss or the first connect is not worth a notice; one that lasts this long is. */
export const NOTICE_DELAY_MS = 1500;

/**
 * Shows that the live connection is being established or restored; nothing while it is up. A polite status region,
 * so a screen reader hears it once without being interrupted. No design exists for it yet: the texts are minimal.
 */
export function ConnectionNotice({ status }: { status: ConnectionStatus }) {
  const t = useTranslate(messages);
  const trouble = status === 'connecting' || status === 'reconnecting';
  const text = status === 'connecting' ? t('connecting') : t('reconnecting');
  // The status the notice was late for; reset when the trouble ends, so a new one waits its delay again.
  const [lateFor, setLateFor] = useState<ConnectionStatus | null>(null);
  useEffect(() => {
    if (!trouble) return;
    const timer = setTimeout(() => {
      setLateFor(status);
    }, NOTICE_DELAY_MS);
    return () => {
      clearTimeout(timer);
      setLateFor(null);
    };
  }, [trouble, status]);

  return (
    <div role="status" className="bm-connection ph-muted">
      {trouble && lateFor === status ? text : ''}
    </div>
  );
}
