import { describe, expect, it } from 'vitest';
import type { ActivityState } from '@backroom/realtime';
import {
  ACTIVE_REFRESH_MS,
  createActivityMonitor,
  createActivityReporter,
  IDLE_AFTER_MS,
  REPORT_GAP_MS,
  type Timers,
} from './activity';

/** A clock and timers the test moves by hand. */
function fakeTimers() {
  let now = 1_000_000;
  let pending: { at: number; callback: () => void; id: number }[] = [];
  let next = 0;
  const timers: Timers = {
    now: () => now,
    setTimer: (callback, ms) => {
      next += 1;
      pending.push({ at: now + ms, callback, id: next });
      return next;
    },
    clearTimer: (handle) => {
      pending = pending.filter((timer) => timer.id !== handle);
    },
  };
  const advance = (ms: number) => {
    const until = now + ms;
    for (;;) {
      pending.sort((a, b) => a.at - b.at);
      const due = pending[0];
      if (!due || due.at > until) break;
      pending.shift();
      now = due.at;
      due.callback();
    }
    now = until;
  };
  return { timers, advance, pendingCount: () => pending.length };
}

function setupMonitor() {
  const clock = fakeTimers();
  const target = new EventTarget();
  const visibilityState: DocumentVisibilityState = 'visible';
  const doc = Object.assign(new EventTarget(), { visibilityState });
  const monitor = createActivityMonitor(target, doc, clock.timers);
  const changes: ActivityState[] = [];
  const off = monitor.subscribe((event) => {
    if (event === 'change') changes.push(monitor.state());
  });
  return { ...clock, target, doc, monitor, changes, off };
}

describe('ActivityMonitor', () => {
  it('Quiet_ForTenMinutes_BecomesIdle_AndInputMakesItActive', () => {
    const { advance, target, monitor, changes } = setupMonitor();
    advance(IDLE_AFTER_MS - 1);
    expect(monitor.state()).toBe('active');

    advance(1);
    expect(monitor.state()).toBe('idle');
    target.dispatchEvent(new Event('keydown'));

    expect(changes).toEqual(['idle', 'active']);
  });

  it('Input_BeforeTheLimit_PostponesIdleWithOneTimer', () => {
    const { advance, target, monitor, pendingCount } = setupMonitor();
    advance(9 * 60_000);
    for (let index = 0; index < 50; index += 1) target.dispatchEvent(new Event('pointermove'));
    expect(pendingCount()).toBe(1);

    advance(9 * 60_000);
    expect(monitor.state()).toBe('active');
    advance(60_000);
    expect(monitor.state()).toBe('idle');
  });

  it('PageBecomingVisible_CountsAsInput', () => {
    const { advance, doc, monitor } = setupMonitor();
    advance(IDLE_AFTER_MS);
    doc.dispatchEvent(new Event('visibilitychange'));

    expect(monitor.state()).toBe('active');
  });

  it('LastSubscriberLeaves_StopsListeningAndClearsTheTimer', () => {
    const { advance, target, monitor, off, pendingCount } = setupMonitor();
    off();
    expect(pendingCount()).toBe(0);
    advance(IDLE_AFTER_MS);
    target.dispatchEvent(new Event('keydown'));

    expect(monitor.state()).toBe('active');
  });
});

describe('ActivityReporter', () => {
  function setupReporter() {
    const clock = fakeTimers();
    const sent: { state: ActivityState; at: number }[] = [];
    const reporter = createActivityReporter((state) => {
      sent.push({ state, at: clock.timers.now() });
    }, clock.timers);
    return { ...clock, reporter, sent };
  }

  it('Changes_WithinTenSeconds_SendTheLatestStateOnceTheGapIsOver', () => {
    const { advance, reporter, sent } = setupReporter();
    reporter.change('idle');
    advance(2_000);
    reporter.input();
    advance(2_000);
    reporter.change('idle');

    expect(sent.map((entry) => entry.state)).toEqual(['idle']);
    advance(REPORT_GAP_MS);
    expect(sent).toEqual([{ state: 'idle', at: 1_000_000 }]);

    reporter.input();
    advance(REPORT_GAP_MS);
    expect(sent.map((entry) => entry.state)).toEqual(['idle', 'active']);
  });

  it('BackAndForthInsideTheGap_SendsNothingWhenTheStateEndsWhereItWas', () => {
    const { advance, reporter, sent } = setupReporter();
    reporter.change('idle');
    reporter.input();
    advance(REPORT_GAP_MS);
    reporter.change('idle');
    advance(1_000);
    reporter.input();
    advance(REPORT_GAP_MS * 3);

    expect(sent.map((entry) => entry.state)).toEqual(['idle', 'active']);
  });

  it('ContinuedInput_SendsActiveAtMostOnceAMinute', () => {
    const { advance, reporter, sent } = setupReporter();
    for (let second = 0; second < 180; second += 1) {
      reporter.input();
      advance(1_000);
    }

    // The connection counts as active from its start, so the first refresh is a minute in.
    expect(sent.map((entry) => entry.at - 1_000_000)).toEqual([ACTIVE_REFRESH_MS, 2 * ACTIVE_REFRESH_MS]);
  });

  it('Connected_WhileIdle_ReportsIdleOnTheNewConnection', () => {
    const { reporter, sent } = setupReporter();
    reporter.change('idle');
    reporter.connected('idle');

    expect(sent.map((entry) => entry.state)).toEqual(['idle', 'idle']);
  });

  it('Dispose_CancelsAHeldReport', () => {
    const { advance, reporter, sent, pendingCount } = setupReporter();
    reporter.change('idle');
    reporter.input();
    expect(pendingCount()).toBe(1);
    reporter.dispose();
    advance(REPORT_GAP_MS);

    expect(sent.map((entry) => entry.state)).toEqual(['idle']);
  });
});
