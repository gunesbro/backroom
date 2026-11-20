import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient, fakeOrganizationsApi } from '@/testing/fakes';
import { OrganizationsApiContext, type OrganizationsApi } from './api';
import { Provisioning, STILL_SETTING_UP_AFTER_MS } from './provisioning';

function renderProvisioning(api: OrganizationsApi, onActive = vi.fn()) {
  render(
    <LocaleProvider locale="en">
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <AuthProvider client={fakeAuthClient()}>
          <OrganizationsApiContext.Provider value={api}>
            <Provisioning id="7" onActive={onActive} onFailed={vi.fn()} onGone={vi.fn()} />
          </OrganizationsApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>,
  );
}

afterEach(() => {
  vi.useRealTimers();
});

describe('Provisioning', () => {
  it('Wait_LongerThanTwoMinutes_SaysThePageCanBeClosed', async () => {
    vi.useFakeTimers();
    const getState = vi.fn((id: string) =>
      Promise.resolve({ id, status: 'provisioning' as const, code: null }),
    );
    const api = fakeOrganizationsApi({ getState });
    renderProvisioning(api);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    expect(screen.getByRole('heading', { name: 'Setting up your organization…' })).toBeInTheDocument();
    expect(screen.queryByText(/Still setting up/)).not.toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(STILL_SETTING_UP_AFTER_MS);
    });

    expect(screen.getByText(/Still setting up\. You can close this page/)).toBeInTheDocument();
    // Polled every 2 s for the first two minutes, not faster.
    expect(getState.mock.calls.length).toBeLessThanOrEqual(STILL_SETTING_UP_AFTER_MS / 2000 + 1);
  });

  it('Wait_WhenTheStateCannotBeRead_StopsAndOffersToTryAgain', async () => {
    const getState = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(403, 'http.403'))
      .mockResolvedValue({ id: '7', status: 'active', code: null });
    const onActive = vi.fn();
    renderProvisioning(fakeOrganizationsApi({ getState }), onActive);

    expect(
      await screen.findByRole('heading', { name: 'Something went wrong. Try again.' }),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    await waitFor(() => {
      expect(onActive).toHaveBeenCalledOnce();
    });
  });
});
