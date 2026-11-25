import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/shared/http';
import { StatusApiContext, type AccountStatus, type StatusApi, type StatusInput } from './api';
import { useSetStatus } from './queries';

const saved: AccountStatus = { dndUntil: null, away: false, customStatus: null, timeZone: 'UTC', version: 2 };

function wrapperFor(api: StatusApi) {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>
      <StatusApiContext.Provider value={api}>{children}</StatusApiContext.Provider>
    </QueryClientProvider>
  );
}

describe('useSetStatus', () => {
  it('sends the status again with UTC when the server does not know the browser zone', async () => {
    const put = vi
      .fn<(input: StatusInput) => Promise<AccountStatus>>()
      .mockRejectedValueOnce(new ApiError(400, 'identity.time_zone_invalid'))
      .mockResolvedValueOnce(saved);
    const api: StatusApi = { get: vi.fn(), put };
    const { result } = renderHook(() => useSetStatus(), { wrapper: wrapperFor(api) });

    await act(() =>
      result.current.mutateAsync({
        dndUntil: null,
        away: false,
        customStatus: null,
        timeZone: 'Mars/Olympus',
      }),
    );

    expect(put).toHaveBeenCalledTimes(2);
    expect(put.mock.calls[1]?.[0].timeZone).toBe('UTC');
  });

  it('does not retry other refusals', async () => {
    const put = vi
      .fn<(input: StatusInput) => Promise<AccountStatus>>()
      .mockRejectedValue(new ApiError(400, 'identity.status_invalid'));
    const api: StatusApi = { get: vi.fn(), put };
    const { result } = renderHook(() => useSetStatus(), { wrapper: wrapperFor(api) });

    await expect(
      act(() =>
        result.current.mutateAsync({
          dndUntil: null,
          away: false,
          customStatus: null,
          timeZone: 'Europe/Istanbul',
        }),
      ),
    ).rejects.toBeInstanceOf(ApiError);
    expect(put).toHaveBeenCalledTimes(1);
  });
});
