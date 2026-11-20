import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/shared/http';
import { fakeAuthClient, fakeOrganizationsApi, organization } from '@/testing/fakes';
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
  window.history.replaceState(null, '', '/');
  localStorage.clear();
  sessionStorage.clear();
});

describe('App', () => {
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
