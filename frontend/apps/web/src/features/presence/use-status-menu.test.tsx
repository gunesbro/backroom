import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MenuEntry } from '@backroom/ui';
import { LocaleContext } from '@/shared/locale';
import { StatusApiContext, type AccountStatus, type StatusApi, type StatusInput } from './api';
import { nextStatus } from './status';
import { useStatusMenu } from './use-status-menu';

const NOW = new Date('2026-11-25T10:00:00Z');
const at = (minutes: number) => new Date(NOW.getTime() + minutes * 60_000).toISOString();

const empty: AccountStatus = {
  dndUntil: null,
  away: false,
  customStatus: null,
  timeZone: 'Europe/Istanbul',
  version: 1,
};

/** A control plane that keeps the status and, like the real one, leaves expired parts out when asked. */
function fakeStatusApi(initial: AccountStatus) {
  let stored = initial;
  const read = (): AccountStatus => {
    const now = new Date();
    const custom = stored.customStatus;
    return {
      ...stored,
      dndUntil: stored.dndUntil && new Date(stored.dndUntil) > now ? stored.dndUntil : null,
      customStatus: custom && (!custom.expiresAt || new Date(custom.expiresAt) > now) ? custom : null,
    };
  };
  return {
    get: vi.fn(() => Promise.resolve(read())),
    put: vi.fn((input: StatusInput) => {
      stored = { ...input, version: stored.version + 1 };
      return Promise.resolve(read());
    }),
  } satisfies StatusApi;
}

function renderMenu(api: StatusApi) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <LocaleContext.Provider value="en">
      <QueryClientProvider client={queryClient}>
        <StatusApiContext.Provider value={api}>{children}</StatusApiContext.Provider>
      </QueryClientProvider>
    </LocaleContext.Provider>
  );
  return renderHook(() => useStatusMenu(), { wrapper });
}

type Item = Extract<MenuEntry, { kind: 'item' }>;

function item(entries: MenuEntry[] | undefined, id: string): Item {
  const found = entries?.find((entry) => entry.kind === 'item' && entry.id === id);
  if (found?.kind !== 'item') throw new Error(`No menu item ${id}.`);
  return found;
}

/** Lets the pending requests and their renders finish without moving the clock. */
const settle = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });

const pass = (ms: number) =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});
afterEach(() => {
  vi.useRealTimers();
});

describe('useStatusMenu', () => {
  it('Away_IsSelectable_AndChoosingItSendsAwayWithoutDoNotDisturb', async () => {
    const api = fakeStatusApi({ ...empty, dndUntil: at(30) });
    const { result } = renderMenu(api);
    await settle();
    expect(result.current?.presence).toBe('dnd');
    const away = item(result.current?.entries, 'away');
    expect(away.disabled).toBeFalsy();
    expect(away.detail).toBe('Until you change it · automatic after 10 min idle');

    act(() => {
      away.onSelect?.();
    });
    await settle();

    expect(api.put).toHaveBeenCalledTimes(1);
    expect(api.put.mock.calls[0]?.[0]).toMatchObject({ away: true, dndUntil: null });
    expect(result.current?.presence).toBe('away');
    expect(result.current?.presenceLabel).toBe('Away');
    expect(item(result.current?.entries, 'away').checked).toBe(true);
    expect(item(result.current?.entries, 'online').checked).toBe(false);
  });

  it('Away_ChosenByHand_StaysWhileThePersonIsActiveUntilOnlineClearsIt', async () => {
    const api = fakeStatusApi({ ...empty, away: true });
    const { result } = renderMenu(api);
    await settle();
    expect(result.current?.presence).toBe('away');

    // Choosing it again changes nothing.
    act(() => {
      item(result.current?.entries, 'away').onSelect?.();
    });
    await settle();
    expect(api.put).not.toHaveBeenCalled();

    act(() => {
      item(result.current?.entries, 'online').onSelect?.();
    });
    await settle();

    expect(api.put.mock.calls[0]?.[0]).toMatchObject({ away: false, dndUntil: null });
    expect(result.current?.presence).toBe('online');
    expect(item(result.current?.entries, 'online').checked).toBe(true);
  });

  it('Online_WhileDoNotDisturbIsOn_ClearsDoNotDisturbAndAway', async () => {
    const api = fakeStatusApi({ ...empty, dndUntil: at(30) });
    const { result } = renderMenu(api);
    await settle();

    act(() => {
      item(result.current?.entries, 'online').onSelect?.();
    });
    await settle();

    expect(api.put.mock.calls[0]?.[0]).toMatchObject({ away: false, dndUntil: null });
    expect(result.current?.presence).toBe('online');
  });

  it('DoNotDisturb_ComesBeforeAway_WhenAStatusCarriesBoth', async () => {
    const api = fakeStatusApi({ ...empty, away: true, dndUntil: at(30) });
    const { result } = renderMenu(api);
    await settle();

    expect(result.current?.presence).toBe('dnd');
    expect(item(result.current?.entries, 'away').checked).toBe(false);
  });

  it('NextStatus_TurningDoNotDisturbOn_ClearsAwayAndKeepsTheCustomStatus', () => {
    const custom = { emoji: '🎯', text: 'Focusing', expiresAt: null };
    const status = { ...empty, away: true, customStatus: custom };

    expect(nextStatus(status, NOW, { dndUntil: at(30) })).toMatchObject({
      dndUntil: at(30),
      away: false,
      customStatus: custom,
    });
    expect(nextStatus(status, NOW, { customStatus: null })).toMatchObject({ away: true, dndUntil: null });
    expect(nextStatus({ ...empty, dndUntil: at(30) }, NOW, { away: true })).toMatchObject({
      away: true,
      dndUntil: null,
    });
  });

  it('DoNotDisturb_WhenItsTimePasses_EndsOnScreenAndTheStatusIsAskedForAgain', async () => {
    const api = fakeStatusApi({ ...empty, dndUntil: at(5) });
    const { result } = renderMenu(api);
    await settle();
    expect(result.current?.presence).toBe('dnd');
    expect(api.get).toHaveBeenCalledTimes(1);

    await pass(5 * 60_000 - 1000);
    expect(result.current?.presence).toBe('dnd');

    await pass(1000);
    expect(result.current?.presence).toBe('online');
    expect(item(result.current?.entries, 'dnd').checked).toBe(false);
    expect(item(result.current?.entries, 'dnd').detail).toBe('30 min · 1 hr · until tomorrow · custom');
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it('CustomStatus_WhenItsTimePasses_DisappearsFromTheMenu', async () => {
    const api = fakeStatusApi({
      ...empty,
      customStatus: { emoji: '🎯', text: 'Focusing', expiresAt: at(5) },
    });
    const { result } = renderMenu(api);
    await settle();
    expect(result.current?.line).toBe('🎯 Focusing · 5 minutes');
    expect(result.current?.entries.some((entry) => entry.id === 'clear-status')).toBe(true);

    await pass(5 * 60_000);

    expect(result.current?.line).toBeUndefined();
    expect(result.current?.entries.some((entry) => entry.id === 'clear-status')).toBe(false);
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it('Unmount_WithATimedStatus_LeavesNoExpiryTimerBehind', async () => {
    const api = fakeStatusApi({ ...empty, dndUntil: at(5) });
    const { unmount } = renderMenu(api);
    await settle();

    unmount();
    await pass(10 * 60_000);

    expect(api.get).toHaveBeenCalledTimes(1);
  });
});
