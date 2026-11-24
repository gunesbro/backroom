import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/shared/http';
import { fakeAuthClient, fakeOrganizationsApi, fakeRealtimeFactory, organization } from '@/testing/fakes';
import { App } from './app';

function renderApp(api = fakeOrganizationsApi(), authClient = fakeAuthClient()) {
  render(<App authClient={authClient} createApi={() => api} />);
  return { api, authClient };
}

const invitationCode = 'abcDEF0123456789_-abcDEF0123456789_-abcDEF0';
const server = setupServer(
  http.post('*/api/v1/invitations/resolve', () =>
    HttpResponse.json({ organizationName: 'Acme', expiresAt: '2026-11-22T14:30:00Z', matchesAccount: true }),
  ),
  http.post('*/api/v1/invitations/accept', () =>
    HttpResponse.json({ organizationId: 'id-Acme', status: 'used' }, { status: 202 }),
  ),
);

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});

afterAll(() => {
  server.close();
});

afterEach(() => {
  server.resetHandlers();
  window.history.replaceState(null, '', '/');
  localStorage.clear();
  sessionStorage.clear();
});

/** Calls to the test cell's address. */
function cellCalls(spy: { mock: { calls: Parameters<typeof fetch>[] } }) {
  return spy.mock.calls.filter(([input]) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    return url.includes('a.example.test');
  });
}

describe('App messaging', () => {
  const list = () => fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) });

  it('Organization_WithTheFlagOff_ShowsOnlyTheNameAndNeverCallsACell', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const authClient = fakeAuthClient();
    render(<App authClient={authClient} createApi={list} />);

    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument();
    expect(cellCalls(fetchSpy)).toHaveLength(0);
    expect(authClient.getAccessToken).not.toHaveBeenCalledWith('cell:a');
    fetchSpy.mockRestore();
  });

  it('Organization_WithACellTokenSource_CallsTheCellWithItsOwnToken', async () => {
    const authorizations: (string | null)[] = [];
    server.use(
      http.get('https://a.example.test/api/v1/orgs/1/channels', ({ request }) => {
        authorizations.push(request.headers.get('Authorization'));
        return HttpResponse.json({ channels: [], next: null });
      }),
    );
    const cellTokens = vi.fn(() => Promise.resolve('cell-a-token'));
    render(
      <App
        authClient={fakeAuthClient()}
        createApi={list}
        cellTokens={cellTokens}
        messagingEnabled
        realtime={fakeRealtimeFactory()}
      />,
    );

    expect(await screen.findByRole('navigation')).toBeInTheDocument();
    await waitFor(() => {
      expect(authorizations).toEqual(['Bearer cell-a-token']);
    });
    expect(cellTokens).toHaveBeenCalledWith('a');
  });
});

describe('App workspace shell', () => {
  const organizations = [organization('1', 'Acme'), organization('2', 'Globex')];

  function renderShell(authClient = fakeAuthClient()) {
    const cellRequests: string[] = [];
    server.use(
      http.get('https://a.example.test/api/v1/orgs/:org/channels', ({ request }) => {
        cellRequests.push(request.url);
        return HttpResponse.json({ channels: [], next: null });
      }),
      http.get('https://a.example.test/api/v1/orgs/:org/members', ({ request }) => {
        cellRequests.push(request.url);
        return HttpResponse.json({ members: [], next: null });
      }),
      http.get('https://a.example.test/api/v1/orgs/:org/invitations', ({ request }) => {
        cellRequests.push(request.url);
        return HttpResponse.json({ invitations: [], next: null });
      }),
    );
    localStorage.setItem('ph.organizations.last', '1');
    render(
      <App
        authClient={authClient}
        createApi={() => fakeOrganizationsApi({ listMine: () => Promise.resolve(organizations) })}
        messagingEnabled
        realtime={fakeRealtimeFactory()}
        invitationsUiEnabled
      />,
    );
    return { authClient, cellRequests };
  }

  it('AccountMenu_SignOut_SignsOut', async () => {
    const { authClient } = renderShell();

    const button = await screen.findByRole('button', { name: 'Status and profile menu' });
    expect(button).toHaveAccessibleDescription('Alice Example');
    fireEvent.click(button);
    fireEvent.click(screen.getByRole('menuitem', { name: 'Sign out' }));

    expect(authClient.signOut).toHaveBeenCalledOnce();
  });

  it('AccountMenu_LaterPhaseItem_IsDisabledAndDoesNothing', async () => {
    const { authClient } = renderShell();

    fireEvent.click(await screen.findByRole('button', { name: 'Status and profile menu' }));
    const profile = screen.getByRole('menuitem', { name: 'Profile and account settings' });
    fireEvent.click(profile);

    expect(profile).toHaveAttribute('aria-disabled', 'true');
    expect(profile).toHaveAccessibleDescription('Coming soon');
    expect(screen.getByRole('menu')).toBeInTheDocument();
    expect(authClient.signOut).not.toHaveBeenCalled();
  });

  it('OrganizationMenu_ChoosingAnother_OpensIt', async () => {
    renderShell();

    fireEvent.click(await screen.findByRole('button', { name: 'Switch organization' }));
    expect(screen.getByRole('menuitemradio', { name: 'Acme' })).toHaveAttribute('aria-checked', 'true');
    fireEvent.click(screen.getByRole('menuitemradio', { name: 'Globex' }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Switch organization' })).toHaveAccessibleDescription(
        'Globex',
      );
    });
    expect(localStorage.getItem('ph.organizations.last')).toBe('2');
  });

  it('OrganizationMenu_Invite_OpensTheInviteDialog', async () => {
    renderShell();

    fireEvent.click(await screen.findByRole('button', { name: 'Switch organization' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Invite people to Acme' }));

    expect(await screen.findByRole('dialog', { name: 'Invite people to Acme' })).toBeInTheDocument();
  });

  it('OrganizationMenu_Create_OpensTheSetUpScreen', async () => {
    renderShell();

    fireEvent.click(await screen.findByRole('button', { name: 'Switch organization' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Create organization' }));

    expect(await screen.findByRole('heading', { name: 'Welcome, Alice' })).toBeInTheDocument();
  });

  it('OrganizationMenu_Keyboard_OpensMovesAndClosesWithEscape', async () => {
    renderShell();
    const button = await screen.findByRole('button', { name: 'Switch organization' });

    fireEvent.keyDown(button, { key: 'ArrowDown' });
    const menu = screen.getByRole('menu');
    const items = Array.from(menu.querySelectorAll<HTMLElement>('[role^="menuitem"]'));
    expect(items[0]).toHaveFocus();
    fireEvent.keyDown(items[0] as HTMLElement, { key: 'ArrowDown' });
    expect(items[1]).toHaveFocus();
    fireEvent.keyDown(items[1] as HTMLElement, { key: 'End' });
    expect(items.at(-1)).toHaveFocus();
    fireEvent.keyDown(items.at(-1) as HTMLElement, { key: 'Escape' });

    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    expect(button).toHaveFocus();
  });

  it('LaterPhaseControls_WhenClicked_StayDisabledAndSendNoRequests', async () => {
    const { cellRequests } = renderShell();
    await screen.findByRole('navigation');
    await waitFor(() => {
      expect(cellRequests.length).toBeGreaterThan(0);
    });
    const before = [...cellRequests];
    const fetchSpy = vi.spyOn(globalThis, 'fetch');

    const passive = Array.from(document.querySelectorAll<HTMLElement>('button[aria-disabled="true"]'));
    expect(passive.length).toBeGreaterThan(10);
    for (const control of passive) fireEvent.click(control);

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(cellRequests).toEqual(before);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    fetchSpy.mockRestore();
  });

  it('Shell_WithTheAccountMenuOpen_HasNoAxeViolations', async () => {
    renderShell();
    fireEvent.click(await screen.findByRole('button', { name: 'Status and profile menu' }));

    const results = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } });

    expect(
      results.violations.map(
        (violation) =>
          `${violation.id}: ${violation.help} ${violation.nodes.map((node) => node.html).join(' | ')}`,
      ),
    ).toEqual([]);
  });

  it('Shell_WithTheOrganizationMenuOpen_HasNoAxeViolations', async () => {
    renderShell();
    fireEvent.click(await screen.findByRole('button', { name: 'Switch organization' }));

    const results = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } });

    expect(
      results.violations.map(
        (violation) =>
          `${violation.id}: ${violation.help} ${violation.nodes.map((node) => node.html).join(' | ')}`,
      ),
    ).toEqual([]);
  });
});

describe('App', () => {
  it('InvitePeople_FirstOpen_KeepsTheWorkspaceOnScreenAndReturnsFocusOnClose', async () => {
    server.use(
      http.get('https://a.example.test/api/v1/orgs/:org/channels', () =>
        HttpResponse.json({ channels: [], next: null }),
      ),
      http.get('https://a.example.test/api/v1/orgs/:org/members', () =>
        HttpResponse.json({ members: [], next: null }),
      ),
      http.get('https://a.example.test/api/v1/orgs/:org/invitations', () =>
        HttpResponse.json({ invitations: [], next: null }),
      ),
    );
    render(
      <App
        authClient={fakeAuthClient()}
        createApi={() =>
          fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) })
        }
        messagingEnabled
        realtime={fakeRealtimeFactory()}
        invitationsUiEnabled
      />,
    );
    // The invite entry is in the organization menu, which returns focus to its button when an item is chosen.
    const opener = await screen.findByRole('button', { name: 'Switch organization' });
    opener.focus();
    fireEvent.click(opener);

    fireEvent.click(screen.getByRole('menuitem', { name: 'Invite people to Acme' }));

    // The dialog's code loads lazily; until it has, the workspace must stay where it is.
    expect(screen.getByRole('heading', { name: 'Channels' })).toBeInTheDocument();
    const dialog = await screen.findByRole('dialog');
    expect(screen.getByRole('heading', { name: 'Channels' })).toBeInTheDocument();

    fireEvent.keyDown(dialog, { key: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(opener).toHaveFocus();
  });

  it('Start_WhileSigningIn_ShowsProgress', () => {
    renderApp(undefined, fakeAuthClient({ start: () => new Promise(() => undefined) }));

    expect(screen.getByRole('heading', { level: 1, name: 'Signing you in…' })).toBeInTheDocument();
  });

  it('Start_WhenSignInFails_OffersToTryAgain', async () => {
    const authClient = fakeAuthClient({ start: () => Promise.reject(new Error('denied')) });
    renderApp(undefined, authClient);

    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent("We couldn't sign you in.");
    expect(authClient.signIn).toHaveBeenCalledOnce();
  });

  it('Start_WithoutOrganizations_CreatesOneAndOpensItWhenReady', async () => {
    const api = fakeOrganizationsApi();
    vi.mocked(api.listMine)
      .mockResolvedValueOnce([])
      .mockResolvedValue([organization('id-Acme', 'Acme')]);
    renderApp(api);

    expect(await screen.findByRole('heading', { name: 'Welcome, Alice' })).toBeInTheDocument();
    expect(screen.getByText('Alice Example')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Organization name'), { target: { value: '  Acme ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Set up organization' }));

    await waitFor(() => {
      expect(api.create).toHaveBeenCalledWith({ name: 'Acme', region: 'eu-dev' });
    });
    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
    expect(localStorage.getItem('ph.organizations.last')).toBe('id-Acme');
    expect(sessionStorage.getItem('ph.organizations.pending')).toBeNull();
  });

  it('Start_WithOneOrganization_OpensIt', async () => {
    renderApp(fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) }));

    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
  });

  it('OpenOrganization_CallsItsCellWithTheCellsOwnToken', async () => {
    const authorizations: (string | null)[] = [];
    server.use(
      http.get('https://a.example.test/api/v1/orgs/:org/channels', ({ request }) => {
        authorizations.push(request.headers.get('Authorization'));
        return HttpResponse.json({ channels: [], next: null });
      }),
      http.get('https://a.example.test/api/v1/orgs/:org/members', () =>
        HttpResponse.json({ members: [], next: null }),
      ),
    );
    const authClient = fakeAuthClient();
    render(
      <App
        authClient={authClient}
        createApi={() =>
          fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) })
        }
        messagingEnabled
        realtime={fakeRealtimeFactory()}
      />,
    );

    await waitFor(() => {
      expect(authorizations).toContain('Bearer token-cell:a');
    });
    expect(authorizations).not.toContain('Bearer token');
    expect(authClient.getAccessToken).toHaveBeenCalledWith('cell:a');
  });

  it('Start_WithSeveralOrganizations_ListsThemAndRemembersTheChoice', async () => {
    const list = [organization('1', 'Acme'), organization('2', 'Globex')];
    renderApp(fakeOrganizationsApi({ listMine: () => Promise.resolve(list) }));

    fireEvent.click(await screen.findByRole('button', { name: /Globex/ }));

    expect(screen.getByRole('heading', { name: 'Globex' })).toBeInTheDocument();
    expect(localStorage.getItem('ph.organizations.last')).toBe('2');
  });

  it('Start_WithARememberedOrganization_OpensIt', async () => {
    localStorage.setItem('ph.organizations.last', '2');
    const list = [organization('1', 'Acme'), organization('2', 'Globex')];
    renderApp(fakeOrganizationsApi({ listMine: () => Promise.resolve(list) }));

    expect(await screen.findByRole('heading', { name: 'Globex' })).toBeInTheDocument();
  });

  it('Start_WhenTheListFails_OffersToTryAgain', async () => {
    const api = fakeOrganizationsApi();
    vi.mocked(api.listMine)
      .mockRejectedValueOnce(new ApiError(403, 'http.403'))
      .mockResolvedValue([organization('1', 'Acme')]);
    renderApp(api);

    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
  });

  it('Start_WhileAnOrganizationIsBeingCreated_WaitsForIt', async () => {
    sessionStorage.setItem('ph.organizations.pending', '7');
    const api = fakeOrganizationsApi({
      getState: () => Promise.resolve({ id: '7', status: 'failed', code: 'delivery_expired' }),
    });
    renderApp(api);

    expect(
      await screen.findByRole('heading', { name: "We couldn't set up the organization." }),
    ).toBeInTheDocument();
    expect(api.listMine).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('heading', { name: 'Welcome, Alice' })).toBeInTheDocument();
    expect(sessionStorage.getItem('ph.organizations.pending')).toBeNull();
  });

  it('Start_WithAnUnknownPendingOrganization_StartsOver', async () => {
    sessionStorage.setItem('ph.organizations.pending', '7');
    renderApp(
      fakeOrganizationsApi({
        getState: () => Promise.reject(new ApiError(404, 'directory.organization_not_found')),
        listMine: () => Promise.resolve([organization('1', 'Acme')]),
      }),
    );

    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
    await waitFor(() => {
      expect(sessionStorage.getItem('ph.organizations.pending')).toBeNull();
    });
  });

  it('SignOut_FromTheFooter_SignsOut', async () => {
    const { authClient } = renderApp(
      fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(authClient.signOut).toHaveBeenCalledOnce();
  });

  it('SignOut_WithAKeptInvitationCode_ForgetsTheCode', async () => {
    renderApp(fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) }));
    const signOut = await screen.findByRole('button', { name: 'Sign out' });
    // Another tab opened an invitation link meanwhile.
    localStorage.setItem(
      'ph.invitations.code',
      JSON.stringify({ code: invitationCode, expiresAt: Date.now() + 60_000 }),
    );

    fireEvent.click(signOut);

    expect(localStorage.getItem('ph.invitations.code')).toBeNull();
  });

  it('Request_WhenRejectedRightAfterSigningInAgain_ShowsTheErrorInsteadOfLooping', async () => {
    sessionStorage.setItem('ph.auth.unauthorized-sign-in-at', String(Date.now() - 5_000));
    const { authClient } = renderApp(
      fakeOrganizationsApi({ listMine: () => Promise.reject(new ApiError(401, 'http.401')) }),
    );

    expect(
      await screen.findByRole('heading', { name: 'Something went wrong. Try again.' }),
    ).toBeInTheDocument();
    expect(authClient.signIn).not.toHaveBeenCalled();
  });

  it('Request_WhenTheServerEndedTheSession_SignsInAgain', async () => {
    const { authClient } = renderApp(
      fakeOrganizationsApi({ listMine: () => Promise.reject(new ApiError(401, 'http.401')) }),
    );

    await waitFor(() => {
      expect(authClient.signIn).toHaveBeenCalledOnce();
    });
  });

  it('Start_WithAPendingInvitation_JoinsAndOpensTheOrganization', async () => {
    localStorage.setItem(
      'ph.invitations.code',
      JSON.stringify({ code: invitationCode, expiresAt: Date.now() + 60_000 }),
    );
    const api = fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('id-Acme', 'Acme')]) });
    renderApp(api);

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
    expect(localStorage.getItem('ph.organizations.last')).toBe('id-Acme');
    expect(localStorage.getItem('ph.invitations.code')).toBeNull();
  });

  it('Start_WithAnExpiredStoredCode_DoesNotOpenTheInvitePage', async () => {
    localStorage.setItem(
      'ph.invitations.code',
      JSON.stringify({ code: invitationCode, expiresAt: Date.now() - 1_000 }),
    );
    renderApp(fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('id-Acme', 'Acme')]) }));

    expect(await screen.findByRole('heading', { name: 'Acme' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Join' })).not.toBeInTheDocument();
    expect(localStorage.getItem('ph.invitations.code')).toBeNull();
  });

  it('Join_FromAPastedLink_OpensTheInvitation', async () => {
    renderApp();

    fireEvent.change(await screen.findByLabelText('Invitation link'), {
      target: { value: `https://app.example.test/invite#${invitationCode}` },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Join' }));

    expect(await screen.findByRole('heading', { name: 'Join Acme' })).toBeInTheDocument();
    expect(JSON.parse(localStorage.getItem('ph.invitations.code') ?? '{}')).toMatchObject({
      code: invitationCode,
    });
  });
});
