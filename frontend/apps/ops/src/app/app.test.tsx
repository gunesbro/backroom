import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { AuthClient } from '@/auth/auth-client';
import type { Cell } from '@/cells/cell-api';
import { App } from './app';

const CELLS_PATH = '/api/v1/ops/cells';

const cellOne: Cell = {
  id: 'eu-1',
  type: 'shared',
  region: 'eu-west',
  status: 'registered',
  version: '1.2.3',
  linkStatus: 'connected',
  lastHeartbeatAt: new Date(Date.now() - 2 * 60_000).toISOString(),
  organizationCount: 7,
  pendingMessages: 2,
  rejectedMessages: 1,
};
const cellTwo: Cell = {
  id: 'eu-2',
  type: 'dedicated',
  region: 'eu-central',
  status: 'registered',
  version: '1.2.0',
  linkStatus: 'disconnected',
  lastHeartbeatAt: new Date(Date.now() - 3 * 3_600_000).toISOString(),
  organizationCount: 0,
  pendingMessages: 0,
  rejectedMessages: 0,
};
const cellThree: Cell = {
  id: 'eu-3',
  type: 'shared',
  region: 'eu-west',
  status: 'pending',
  version: null,
  linkStatus: 'disconnected',
  lastHeartbeatAt: null,
  organizationCount: 0,
  pendingMessages: 0,
  rejectedMessages: 0,
};

const requests: { token: string | null; after: string | null }[] = [];
const server = setupServer();

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});
afterEach(() => {
  server.resetHandlers();
  requests.length = 0;
  vi.useRealTimers();
});
afterAll(() => {
  server.close();
});

function respondWith(cells: Cell[]) {
  server.use(
    http.get(CELLS_PATH, ({ request }) => {
      requests.push({
        token: request.headers.get('Authorization'),
        after: new URL(request.url, 'http://localhost').searchParams.get('after'),
      });
      return HttpResponse.json({ cells, next: null });
    }),
  );
}

function fakeAuth(): AuthClient {
  return {
    start: () => Promise.resolve({ name: 'Ada Ops' }),
    signIn: () => Promise.resolve(),
    getAccessToken: () => Promise.resolve('ops-token'),
    signOut: () => Promise.resolve(),
  };
}

function setVisibility(state: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  act(() => {
    document.dispatchEvent(new Event('visibilitychange'));
  });
}

describe('Cell list', () => {
  it('shows every field of every cell and sends the control-plane token', async () => {
    respondWith([cellOne, cellTwo, cellThree]);

    render(<App auth={fakeAuth()} locale="en" />);

    const table = await screen.findByRole('table', { name: 'Cells and their link status' });
    expect(table).toBeInTheDocument();
    const first = within(rowOf('eu-1'));
    expect(first.getByText('shared')).toBeInTheDocument();
    expect(first.getByText('eu-west')).toBeInTheDocument();
    expect(first.getByText('1.2.3')).toBeInTheDocument();
    expect(first.getByText('2 minutes ago')).toBeInTheDocument();
    expect(first.getByText(/Connected/)).toBeInTheDocument();
    const cells = first.getAllByRole('cell');
    expect(cells.slice(-3).map((cell) => cell.textContent)).toEqual(['7', '2', '1']);
    // The absolute time uses an explicit locale, not the machine's.
    const heartbeat = first.getByText('2 minutes ago');
    expect(heartbeat.querySelector('small')?.textContent).toMatch(/\d{4} at \d{1,2}:\d{2}\s?(AM|PM)/);
    expect(requests[0]?.token).toBe('Bearer ops-token');
  });

  it('labels link status in words, not by color alone', async () => {
    respondWith([cellOne, cellTwo, cellThree]);

    render(<App auth={fakeAuth()} locale="en" />);

    await screen.findByRole('table');
    expect(within(rowOf('eu-1')).getByText(/^\s*Connected/)).toBeInTheDocument();
    expect(within(rowOf('eu-2')).getByText(/Disconnected/)).toBeInTheDocument();
    expect(within(rowOf('eu-3')).getByText('Awaiting registration')).toBeInTheDocument();
    expect(within(rowOf('eu-3')).getByText('Never')).toBeInTheDocument();
  });

  it('shows a capped message count as 10,000+', async () => {
    respondWith([{ ...cellOne, pendingMessages: 10_001, rejectedMessages: 12_345 }]);

    render(<App auth={fakeAuth()} locale="en" />);

    await screen.findByRole('table');
    const cells = within(rowOf('eu-1')).getAllByRole('cell');
    expect(cells.slice(-2).map((cell) => cell.textContent)).toEqual(['10,000+', '10,000+']);
  });

  it('shows Turkish texts and a Turkish absolute time', async () => {
    respondWith([cellOne]);

    render(<App auth={fakeAuth()} locale="tr" />);

    expect(await screen.findByRole('table', { name: 'Hücreler ve bağlantı durumları' })).toBeInTheDocument();
    expect(screen.getByText(/Bağlı/)).toBeInTheDocument();
    expect(screen.getByText('2 dakika önce')).toBeInTheDocument();
    expect(screen.getByText(/\d{4} \d{2}:\d{2}/)).toBeInTheDocument();
  });

  it('shows the loading state first and the empty state without cells', async () => {
    respondWith([]);

    render(<App auth={fakeAuth()} locale="en" />);

    expect(await screen.findByText('Loading cells…')).toBeInTheDocument();
    expect(await screen.findByText('No cells yet.')).toBeInTheDocument();
  });

  it('explains a 403 as missing ops role and two-step verification', async () => {
    server.use(http.get(CELLS_PATH, () => new HttpResponse(null, { status: 403 })));

    render(<App auth={fakeAuth()} locale="en" />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Ops role and two-step verification required');
  });

  it('shows the same 403 text in Turkish', async () => {
    server.use(http.get(CELLS_PATH, () => new HttpResponse(null, { status: 403 })));

    render(<App auth={fakeAuth()} locale="tr" />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Ops rolü ve iki adımlı doğrulama gerekli');
  });

  it('shows an error with a retry for other failures', async () => {
    server.use(http.get(CELLS_PATH, () => new HttpResponse(null, { status: 500 })));

    render(<App auth={fakeAuth()} locale="en" />);

    expect(await screen.findByRole('alert', {}, { timeout: 5_000 })).toHaveTextContent(
      'The cells could not be loaded.',
    );
    respondWith([cellOne]);
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('table')).toBeInTheDocument();
  });

  it('reloads on the refresh button', async () => {
    respondWith([cellOne]);
    render(<App auth={fakeAuth()} locale="en" />);
    await screen.findByRole('table');

    respondWith([cellOne, cellTwo]);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByRole('rowheader', { name: 'eu-2' })).toBeInTheDocument();
  });

  it('refreshes every 30 seconds only while the tab is visible', async () => {
    respondWith([cellOne]);
    render(<App auth={fakeAuth()} locale="en" />);
    await screen.findByRole('table');
    // Only the interval is fake, and before the option is switched on, so the query uses the fake one.
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    fireEvent.click(screen.getByRole('checkbox', { name: /every 30 seconds/ }));
    expect(requests).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(30_000);
    await waitFor(() => {
      expect(requests).toHaveLength(2);
    });

    setVisibility('hidden');
    await vi.advanceTimersByTimeAsync(120_000);
    expect(requests).toHaveLength(2);

    setVisibility('visible');
    await vi.advanceTimersByTimeAsync(30_000);
    await waitFor(() => {
      expect(requests).toHaveLength(3);
    });
  });

  it('does not refresh by itself when the option is off', async () => {
    respondWith([cellOne]);
    render(<App auth={fakeAuth()} locale="en" />);
    await screen.findByRole('table');

    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    await vi.advanceTimersByTimeAsync(120_000);

    expect(requests).toHaveLength(1);
  });

  it('has no accessibility violations', async () => {
    respondWith([cellOne, cellTwo, cellThree]);
    const { container } = render(<App auth={fakeAuth()} locale="en" />);
    await screen.findByRole('table');

    const results = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } });

    expect(results.violations.map((violation) => violation.id)).toEqual([]);
  });
});

function rowOf(id: string): HTMLElement {
  const row = screen.getByRole('rowheader', { name: id }).closest('tr');
  if (!row) throw new Error(`No row for ${id}.`);
  return row;
}
