import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Locale } from '@backroom/i18n';
import { AuthProvider } from '@/features/auth';
import { StatusApiContext, type AccountStatus, type StatusApi, type StatusInput } from '@/features/presence';
import { ApiError } from '@/shared/http';
import { LocaleContext } from '@/shared/locale';
import { fakeAuthClient } from '@/testing/fakes';
import { AccountBar } from './workspace-chrome';

const NOW = new Date('2026-11-24T10:00:00Z');

const empty: AccountStatus = {
  dndUntil: null,
  away: false,
  customStatus: null,
  timeZone: 'Europe/Istanbul',
  version: 1,
};

function fakeStatusApi(initial: AccountStatus = empty) {
  let version = initial.version;
  const api = {
    get: vi.fn(() => Promise.resolve(initial)),
    put: vi.fn((input: StatusInput) => {
      version += 1;
      return Promise.resolve<AccountStatus>({ ...input, version });
    }),
  } satisfies StatusApi;
  return api;
}

function renderBar(api: StatusApi | null, locale: Locale = 'en') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <LocaleContext.Provider value={locale}>
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={fakeAuthClient()}>
          <StatusApiContext.Provider value={api}>
            <AccountBar />
          </StatusApiContext.Provider>
        </AuthProvider>
      </QueryClientProvider>
    </LocaleContext.Provider>,
  );
}

async function openMenu(name = /status and profile menu/i) {
  fireEvent.click(await screen.findByRole('button', { name }));
  return screen.getByRole('menu');
}

const lastPut = (api: ReturnType<typeof fakeStatusApi>) => api.put.mock.calls.at(-1)?.[0];

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
});
afterEach(() => {
  vi.useRealTimers();
});

describe('AccountBar status menu', () => {
  it('Menu_WhenPresenceIsOff_KeepsTheStatusItemsPassive', async () => {
    renderBar(null);
    const menu = await openMenu();

    for (const name of ['Online', 'Away', 'Do not disturb', 'Set a custom status…']) {
      expect(within(menu).getByRole('menuitem', { name })).toHaveAttribute('aria-disabled', 'true');
    }
  });

  it('DoNotDisturb_ThirtyMinutes_SendsTheEndTimeAndTheTimeZone', async () => {
    const api = fakeStatusApi();
    renderBar(api);
    const menu = await openMenu();
    fireEvent.click(within(menu).getByRole('menuitemradio', { name: /^Do not disturb/ }));
    const dialog = screen.getByRole('dialog', { name: 'Do not disturb' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on' }));

    await waitFor(() => {
      expect(api.put).toHaveBeenCalledTimes(1);
    });
    expect(lastPut(api)).toEqual({
      dndUntil: '2026-11-24T10:30:00.000Z',
      away: false,
      customStatus: null,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    });
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it.each([
    ['1 hour', () => new Date(NOW.getTime() + 60 * 60_000)],
    [
      'Until tomorrow',
      () => {
        const next = new Date(NOW);
        next.setDate(next.getDate() + 1);
        next.setHours(9, 0, 0, 0);
        return next;
      },
    ],
  ])('DoNotDisturb_%s_SendsItsEndTime', async (choice, expected) => {
    const api = fakeStatusApi();
    renderBar(api);
    fireEvent.click(within(await openMenu()).getByRole('menuitemradio', { name: /^Do not disturb/ }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('radio', { name: choice }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on' }));

    await waitFor(() => {
      expect(lastPut(api)?.dndUntil).toBe(expected().toISOString());
    });
  });

  it('DoNotDisturb_CustomTimeBeyondSevenDays_IsRefusedWithoutAsking', async () => {
    const api = fakeStatusApi();
    renderBar(api);
    fireEvent.click(within(await openMenu()).getByRole('menuitemradio', { name: /^Do not disturb/ }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('radio', { name: 'Custom' }));
    fireEvent.change(within(dialog).getByLabelText('Until'), { target: { value: '2026-12-09T10:00' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Turn on' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Choose a time in the next 7 days.');
    expect(api.put).not.toHaveBeenCalled();
  });

  it('Online_WhileDoNotDisturbIsOn_TurnsItOffAndKeepsTheCustomStatus', async () => {
    const custom = { emoji: '🎯', text: 'Focusing', expiresAt: null };
    const api = fakeStatusApi({ ...empty, dndUntil: '2026-11-24T11:00:00Z', customStatus: custom });
    renderBar(api);
    await screen.findByText('🎯 Focusing');
    const menu = await openMenu();
    expect(within(menu).getByRole('menuitemradio', { name: /^Do not disturb/ })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    fireEvent.click(within(menu).getByRole('menuitemradio', { name: 'Online' }));

    await waitFor(() => {
      expect(lastPut(api)).toMatchObject({ dndUntil: null, customStatus: custom });
    });
  });

  it('Away_CanBeChosen_AndTurnsDoNotDisturbOffWhileKeepingTheCustomStatus', async () => {
    const custom = { emoji: '🎯', text: 'Focusing', expiresAt: null };
    const api = fakeStatusApi({ ...empty, dndUntil: '2026-11-24T11:00:00Z', customStatus: custom });
    renderBar(api);
    await screen.findByText('🎯 Focusing');
    const away = within(await openMenu()).getByRole('menuitemradio', { name: /^Away/ });
    expect(away).not.toHaveAttribute('aria-disabled', 'true');
    expect(away).toHaveTextContent('Until you change it · automatic after 10 min idle');
    fireEvent.click(away);

    await waitFor(() => {
      expect(lastPut(api)).toEqual({
        dndUntil: null,
        away: true,
        customStatus: custom,
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      });
    });
    expect(within(await openMenu()).getByRole('menuitemradio', { name: /^Away/ })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });

  it('Online_WhileAwayWasChosen_ClearsIt', async () => {
    const api = fakeStatusApi({ ...empty, away: true });
    renderBar(api);
    const menu = await openMenu();
    await waitFor(() => {
      expect(within(menu).getByRole('menuitemradio', { name: /^Away/ })).toHaveAttribute(
        'aria-checked',
        'true',
      );
    });
    fireEvent.click(within(menu).getByRole('menuitemradio', { name: 'Online' }));

    await waitFor(() => {
      expect(lastPut(api)).toMatchObject({ dndUntil: null, away: false });
    });
  });

  it('DoNotDisturb_WhileAwayWasChosen_ClearsAway', async () => {
    const api = fakeStatusApi({ ...empty, away: true });
    renderBar(api);
    const menu = await openMenu();
    fireEvent.click(within(menu).getByRole('menuitemradio', { name: /^Do not disturb/ }));
    fireEvent.click(
      within(screen.getByRole('dialog', { name: 'Do not disturb' })).getByRole('button', { name: 'Turn on' }),
    );

    await waitFor(() => {
      expect(lastPut(api)).toMatchObject({ dndUntil: '2026-11-24T10:30:00.000Z', away: false });
    });
  });

  it('ClearStatus_RemovesTheCustomStatusAndKeepsDoNotDisturb', async () => {
    const api = fakeStatusApi({
      ...empty,
      dndUntil: '2026-11-24T11:00:00Z',
      customStatus: { emoji: '🎯', text: 'Focusing', expiresAt: '2026-11-24T11:00:00Z' },
    });
    renderBar(api);
    expect(await screen.findByText('🎯 Focusing · 1 hour')).toBeInTheDocument();
    fireEvent.click(within(await openMenu()).getByRole('menuitem', { name: 'Clear status' }));

    await waitFor(() => {
      expect(lastPut(api)).toMatchObject({ dndUntil: '2026-11-24T11:00:00Z', customStatus: null });
    });
    expect(await screen.findByText('alice@example.test')).toBeInTheDocument();
  });

  it('CustomStatus_Validation_RefusesBadInputBeforeAsking', async () => {
    const api = fakeStatusApi();
    renderBar(api);
    fireEvent.click(within(await openMenu()).getByRole('menuitem', { name: 'Set a custom status…' }));
    const dialog = screen.getByRole('dialog', { name: 'Custom status' });
    const save = within(dialog).getByRole('button', { name: 'Save' });

    fireEvent.click(save);
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Add an emoji or a text.');

    fireEvent.change(within(dialog).getByLabelText("What's your status?"), {
      target: { value: 'x'.repeat(101) },
    });
    expect(within(dialog).getByText('101/100')).toBeInTheDocument();
    fireEvent.click(save);
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Use at most 100 characters, on one line.',
    );

    fireEvent.change(within(dialog).getByLabelText("What's your status?"), { target: { value: 'Focusing' } });
    fireEvent.change(within(dialog).getByLabelText('Emoji'), { target: { value: '🎯🎯' } });
    fireEvent.click(save);
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Use exactly one emoji.');
    expect(api.put).not.toHaveBeenCalled();
  });

  it('CustomStatus_WithAHundredCharactersAndAnHour_IsSaved', async () => {
    const api = fakeStatusApi();
    renderBar(api);
    fireEvent.click(within(await openMenu()).getByRole('menuitem', { name: 'Set a custom status…' }));
    const dialog = screen.getByRole('dialog');
    const text = `  ${'ş'.repeat(100)}  `;
    fireEvent.change(within(dialog).getByLabelText('Emoji'), { target: { value: '👍🏽' } });
    fireEvent.change(within(dialog).getByLabelText("What's your status?"), { target: { value: text } });
    fireEvent.click(within(dialog).getByRole('radio', { name: '1 hour' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(lastPut(api)?.customStatus).toEqual({
        emoji: '👍🏽',
        text: 'ş'.repeat(100),
        expiresAt: '2026-11-24T11:00:00.000Z',
      });
    });
  });

  it('Change_WhenRateLimited_SaysSoAndPutsTheOldStatusBack', async () => {
    const api = fakeStatusApi();
    api.put.mockRejectedValueOnce(new ApiError(429, 'identity.rate_limited', 60));
    renderBar(api);
    fireEvent.click(within(await openMenu()).getByRole('menuitemradio', { name: /^Do not disturb/ }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Turn on' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Too many changes. Wait a minute and try again.',
    );
    const menu = await openMenu();
    expect(within(menu).getByRole('menuitemradio', { name: 'Online' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });

  it('Menu_InTurkish_UsesTheDesignTexts', async () => {
    renderBar(fakeStatusApi(), 'tr');
    const menu = await openMenu(/durum ve profil menüsü/i);

    expect(within(menu).getByRole('menuitemradio', { name: 'Çevrimiçi' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitemradio', { name: /^Boşta/ })).toHaveTextContent(
      'Sen değiştirene kadar · 10 dk hareketsizlikte otomatik',
    );
    expect(within(menu).getByRole('menuitemradio', { name: /^Rahatsız etme/ })).toHaveTextContent(
      '30 dk · 1 sa · yarına kadar · özel',
    );
    expect(within(menu).getByRole('menuitem', { name: 'Özel durum ayarla…' })).toBeInTheDocument();
  });

  it.each(['en', 'tr'] as const)('Accessibility_OpenMenuAndDialogs_HaveNoViolations_%s', async (locale) => {
    vi.useRealTimers();
    const api = fakeStatusApi({
      ...empty,
      dndUntil: new Date(Date.now() + 60 * 60_000).toISOString(),
      customStatus: { emoji: '🎯', text: 'Focusing', expiresAt: null },
    });
    const { container } = renderBar(api, locale);
    await screen.findByText('🎯 Focusing');
    await openMenu(locale === 'en' ? /status and profile menu/i : /durum ve profil menüsü/i);
    const rules = { rules: { 'color-contrast': { enabled: false } } };
    expect((await axe.run(container, rules)).violations).toEqual([]);

    // The first entry is the custom status itself; choosing it opens the dialog to edit it.
    act(() => {
      fireEvent.click(screen.getByRole('menuitem', { name: '🎯 Focusing' }));
    });
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect((await axe.run(document.body, rules)).violations).toEqual([]);
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });

    await openMenu(locale === 'en' ? /status and profile menu/i : /durum ve profil menüsü/i);
    fireEvent.click(
      screen.getByRole('menuitemradio', { name: locale === 'en' ? /^Do not disturb/ : /^Rahatsız etme/ }),
    );
    expect((await axe.run(document.body, rules)).violations).toEqual([]);
  });
});
