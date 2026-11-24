import { useEffect, useSyncExternalStore } from 'react';
import type { ActivityState, RealtimeClient } from '@backroom/realtime';
import { activityMonitor, createActivityReporter, type ActivityMonitor } from './activity';
import { usePresenceEnabled } from './api';

/** Re-renders on state changes only, not on every input. */
const subscribeShared = (notify: () => void) =>
  activityMonitor().subscribe((event) => {
    if (event === 'change') notify();
  });

/** Whether the person is active in this window or idle (10 minutes without input); `active` while presence is off. */
export function useLocalActivity(): ActivityState {
  const enabled = usePresenceEnabled();
  const state = useSyncExternalStore(enabled ? subscribeShared : noSubscription, () =>
    activityMonitor().state(),
  );
  return enabled ? state : 'active';
}

const noSubscription = () => () => undefined;

/**
 * Reports the person's activity to one cell's hub (`ReportActivity`, flow 9): idle after 10 minutes without input,
 * active again on input, throttled to the hub's limit. Nothing is sent while presence is off.
 */
export function useActivityReporting(
  client: RealtimeClient | null,
  monitor: ActivityMonitor = activityMonitor(),
) {
  const enabled = usePresenceEnabled();
  useEffect(() => {
    if (!enabled || !client) return;
    const reporter = createActivityReporter((state) => {
      client.reportActivity(state);
    });
    const offMonitor = monitor.subscribe((event) => {
      if (event === 'input') reporter.input();
      else reporter.change(monitor.state());
    });
    const offStatus = client.onStatus((status) => {
      if (status === 'connected') reporter.connected(monitor.state());
    });
    return () => {
      offMonitor();
      offStatus();
      reporter.dispose();
    };
  }, [enabled, client, monitor]);
}
