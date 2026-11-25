import { useEffect, useRef, useState } from 'react';
import { expiryClock, type ExpiryClock } from './expiry-clock';

/**
 * The time to judge timed statuses by. The component is drawn again when one of `times` (ISO 8601 ends of do not
 * disturb or a custom status; null for none) passes, and `onExpire` is called then (to ask the server again, for
 * example). Every caller shares one timer (`expiryClock`); `key` names the caller's set of times ("me", or one list
 * of people), and the timer is dropped when the last caller unmounts.
 */
export function useExpiryClock(
  key: string,
  times: readonly (string | null | undefined)[],
  onExpire?: () => void,
  clock: ExpiryClock = expiryClock,
): Date {
  const [, setTick] = useState(0);
  const expired = useRef(onExpire);
  useEffect(() => {
    expired.current = onExpire;
  });
  // The times as one string, so a new array with the same times does not restart the watch.
  const signature = times.filter(Boolean).join('|');
  useEffect(() => {
    if (signature === '') return;
    return clock.watch(key, signature.split('|'), () => {
      setTick((tick) => tick + 1);
      expired.current?.();
    });
  }, [clock, key, signature]);
  return new Date();
}
