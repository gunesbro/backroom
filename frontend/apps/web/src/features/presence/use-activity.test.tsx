import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { ActivityState, ConnectionStatus, RealtimeClient } from '@backroom/realtime';
import type { ActivityMonitor } from './activity';
import { StatusApiContext, type StatusApi } from './api';
import { useActivityReporting } from './use-activity';

function fakeMonitor() {
  let state: ActivityState = 'active';
  const listeners = new Set<(event: 'input' | 'change') => void>();
  const monitor: ActivityMonitor = {
    state: () => state,
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
  const become = (next: ActivityState) => {
    state = next;
    for (const listener of listeners) listener('change');
  };
  return { monitor, become, listeners };
}

function fakeClient() {
  const statusListeners = new Set<(status: ConnectionStatus, reconnected: boolean) => void>();
  const client = {
    reportActivity: vi.fn(),
    onStatus: (listener: (status: ConnectionStatus, reconnected: boolean) => void) => {
      statusListeners.add(listener);
      return () => statusListeners.delete(listener);
    },
  };
  return { client: client as unknown as RealtimeClient, spies: client, statusListeners };
}

const api: StatusApi = { get: vi.fn(), put: vi.fn() };
const on = ({ children }: { children: ReactNode }) => (
  <StatusApiContext.Provider value={api}>{children}</StatusApiContext.Provider>
);

describe('useActivityReporting', () => {
  it('Idle_IsReportedToTheCell_AndListenersAreRemovedOnUnmount', () => {
    const { monitor, become, listeners } = fakeMonitor();
    const { client, spies, statusListeners } = fakeClient();
    const { unmount } = renderHook(
      () => {
        useActivityReporting(client, monitor);
      },
      { wrapper: on },
    );

    act(() => {
      become('idle');
    });
    expect(spies.reportActivity).toHaveBeenCalledWith('idle');

    unmount();
    expect(listeners.size).toBe(0);
    expect(statusListeners.size).toBe(0);
  });

  it('PresenceOff_ReportsNothing', () => {
    const { monitor, become, listeners } = fakeMonitor();
    const { client, spies } = fakeClient();
    renderHook(() => {
      useActivityReporting(client, monitor);
    });

    act(() => {
      become('idle');
    });
    expect(spies.reportActivity).not.toHaveBeenCalled();
    expect(listeners.size).toBe(0);
  });
});
