import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MemberPresence } from './api';
import { createExpiryClock, MAX_TIMER_MS } from './expiry-clock';
import { currentPresence, presenceExpiries } from './status';
import { useExpiryClock } from './use-expiry-clock';

const NOW = new Date('2026-11-25T10:00:00Z');
const at = (minutes: number) => new Date(NOW.getTime() + minutes * 60_000).toISOString();
const DAY = 24 * 60 * 60_000;

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});
afterEach(() => {
  vi.useRealTimers();
});

describe('expiry clock', () => {
  it('Watch_OfSeveralSources_UsesOneTimerAndTellsOnlyTheSourceWhoseTimePassed', () => {
    const clock = createExpiryClock();
    const mine = vi.fn();
    const people = vi.fn();
    clock.watch('me', [at(30), null, undefined], mine);
    clock.watch('people', [at(5), at(45), at(-1), 'not a date'], people);

    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(5 * 60_000 - 1);
    expect(people).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(people).toHaveBeenCalledTimes(1);
    expect(mine).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(1);

    vi.advanceTimersByTime(25 * 60_000);
    expect(mine).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(15 * 60_000);
    expect(people).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('Watch_OfATimeBeyondTheLongestTimerDelay_WaitsInStepsAndFiresOnTime', () => {
    const setTimeoutSpy = vi.spyOn(globalThis, 'setTimeout');
    const clock = createExpiryClock();
    const listener = vi.fn();
    clock.watch('me', [new Date(NOW.getTime() + 30 * DAY).toISOString()], listener);

    expect(setTimeoutSpy).toHaveBeenLastCalledWith(expect.any(Function), MAX_TIMER_MS);
    vi.advanceTimersByTime(MAX_TIMER_MS);
    expect(listener).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(30 * DAY - MAX_TIMER_MS);
    expect(listener).toHaveBeenCalledTimes(1);
    setTimeoutSpy.mockRestore();
  });

  it('Unwatch_OfTheLastSource_ClearsTheTimer', () => {
    const clock = createExpiryClock();
    const listener = vi.fn();
    const unwatchMine = clock.watch('me', [at(10)], listener);
    const unwatchPeople = clock.watch('people', [at(20)], listener);

    unwatchMine();
    expect(vi.getTimerCount()).toBe(1);
    unwatchPeople();
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(60 * 60_000);
    expect(listener).not.toHaveBeenCalled();
  });

  it('Watch_WithNothingDueToEnd_StartsNoTimer', () => {
    const clock = createExpiryClock();
    clock.watch('me', [null, at(-5)], vi.fn());

    expect(vi.getTimerCount()).toBe(0);
  });
});

function People({
  people,
  clock,
}: {
  people: MemberPresence[];
  clock: ReturnType<typeof createExpiryClock>;
}) {
  const now = useExpiryClock('people:o1', presenceExpiries(people), undefined, clock);
  return (
    <ul>
      {people.map((entry) => {
        const person = currentPresence(entry, now);
        return (
          <li key={person.accountId} data-testid={person.accountId}>
            {person.dndUntil ? 'dnd' : person.state}
            {person.customStatus ? ` ${person.customStatus.text ?? ''}` : ''}
          </li>
        );
      })}
    </ul>
  );
}

describe('watched people', () => {
  const people: MemberPresence[] = [
    { accountId: 'alice', state: 'online', dndUntil: at(30), customStatus: null },
    {
      accountId: 'bob',
      state: 'idle',
      dndUntil: null,
      customStatus: { emoji: null, text: 'Lunch', expiresAt: at(60) },
    },
    { accountId: 'carol', state: 'online', dndUntil: null, customStatus: null },
  ];

  it('DoNotDisturbAndCustomStatus_OfWatchedPeople_EndOnScreenWithoutAnEventFromTheServer', () => {
    const clock = createExpiryClock();
    const { unmount } = render(<People people={people} clock={clock} />);
    expect(screen.getByTestId('alice')).toHaveTextContent('dnd');
    expect(screen.getByTestId('bob')).toHaveTextContent('idle Lunch');
    expect(vi.getTimerCount()).toBe(1);

    act(() => {
      vi.advanceTimersByTime(30 * 60_000);
    });
    expect(screen.getByTestId('alice')).toHaveTextContent('online');
    expect(screen.getByTestId('bob')).toHaveTextContent('idle Lunch');

    act(() => {
      vi.advanceTimersByTime(30 * 60_000);
    });
    expect(screen.getByTestId('bob')).toHaveTextContent(/^idle$/);
    expect(vi.getTimerCount()).toBe(0);

    unmount();
  });

  it('Unmount_ClearsTheTimer', () => {
    const clock = createExpiryClock();
    const { unmount } = render(<People people={people} clock={clock} />);
    expect(vi.getTimerCount()).toBe(1);

    unmount();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('CurrentPresence_WithNothingExpired_ReturnsTheSameEntry', () => {
    expect(currentPresence(people[0] as MemberPresence, NOW)).toBe(people[0]);
  });
});
