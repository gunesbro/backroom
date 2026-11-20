import { focusManager, onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { http, HttpResponse, type JsonBodyType } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { formatDateTime, type Locale } from '@backroom/i18n';
import { AuthProvider, type AuthClient } from '@/features/auth';
import { LocaleProvider } from '@/shared/locale-provider';
import { fakeAuthClient } from '@/testing/fakes';
import { acceptancePollDelay } from './acceptance-polling';
import { InvitePage } from './invite-page';
import {
  captureInvitationCode,
  INVITATION_ACCEPTING_KEY,
  INVITATION_CODE_KEY,
  leaveInvitePath,
  readInvitationCode,
  saveInvitationCode,
} from './pending-invitation';

const BASE = 'https://cp.example.test';
const code = 'abcDEF0123456789_-abcDEF0123456789_-abcDEF0';
const organizationId = '0b6f3c6e-6a49-4c1e-9d0a-3f1f0c2b7a11';
const expiresAt = '2026-11-22T14:30:00Z';

type Seen = { method: string; url: string; body: string; authorization: string | null };
let seen: Seen[] = [];

const server = setupServer();

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});

afterEach(() => {
  server.resetHandlers();
  seen = [];
  sessionStorage.clear();
  localStorage.clear();
  focusManager.setFocused(undefined);
  onlineManager.setOnline(true);
  window.history.replaceState(null, '', '/');
});

afterAll(() => {
  server.close();
});

type Reply = { status?: number; body: JsonBodyType; headers?: Record<string, string> };

/** Answers each call to a path with the next reply (the last one repeats), recording every request. */
function serve(method: 'get' | 'post', path: string, ...replies: Reply[]) {
  let calls = 0;
  server.use(
    http[method](`${BASE}${path}`, async ({ request }) => {
      seen.push({
        method: request.method,
        url: request.url,
        body: await request.text(),
        authorization: request.headers.get('Authorization'),
      });
      const reply = replies[Math.min(calls, replies.length - 1)] ?? { body: null };
      calls += 1;
      return HttpResponse.json(reply.body, { status: reply.status ?? 200, headers: reply.headers ?? {} });
    }),
  );
}

const problem = (status: number, problemCode: string, headers?: Record<string, string>): Reply => ({
  status,
  body: { status, code: problemCode },
  ...(headers ? { headers } : {}),
});

function resolves(matchesAccount = true) {
  serve('post', '/api/v1/invitations/resolve', {
    body: { organizationName: 'Acme', expiresAt, matchesAccount },
  });
}

function calls(path: string) {
  return seen.filter((request) => request.url === `${BASE}${path}`);
}

function renderPage({
  locale = 'en',
  pollDelay = (polls: number) => (polls >= 20 ? false : 5),
  authClient = fakeAuthClient(),
}: {
  locale?: Locale;
  pollDelay?: (polls: number) => number | false;
  authClient?: AuthClient;
} = {}) {
  const onDone = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <LocaleProvider locale={locale}>
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={authClient}>
          <InvitePage onDone={onDone} baseUrl={BASE} pollDelay={pollDelay} />
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>,
  );
  return { onDone, authClient, container: view.container };
}

async function expectAccessible(container: HTMLElement) {
  // jsdom cannot compute colors; contrast is checked on the design tokens instead.
  const results = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } });
  expect(results.violations.map(({ id, help }) => `${id}: ${help}`)).toEqual([]);
}

describe('InvitePage', () => {
  it('Open_WithAValidInvitation_ShowsTheOrganizationAndExpiryWithoutAccepting', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', {
      status: 202,
      body: { organizationId, status: 'accepting' },
    });
    const { container } = renderPage();

    const heading = await screen.findByRole('heading', { level: 1, name: 'Join Acme' });
    expect(heading).toHaveFocus();
    expect(
      screen.getByText(`This invitation is valid until ${formatDateTime(new Date(expiresAt), 'en')}.`),
    ).toBeInTheDocument();
    expect(screen.getByText("You'll join as alice@example.test")).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Join' })).toBeEnabled();
    expect(calls('/api/v1/invitations/accept')).toHaveLength(0);
    expect(seen).toEqual([
      {
        method: 'POST',
        url: `${BASE}/api/v1/invitations/resolve`,
        body: JSON.stringify({ code }),
        authorization: 'Bearer token',
      },
    ]);
    await expectAccessible(container);
  });

  it('Open_InTurkish_FormatsTheExpiryInTurkish', async () => {
    saveInvitationCode(code);
    resolves();
    renderPage({ locale: 'tr' });

    expect(await screen.findByRole('heading', { name: 'Acme organizasyonuna katıl' })).toBeInTheDocument();
    expect(
      screen.getByText(`Bu davet ${formatDateTime(new Date(expiresAt), 'tr')} tarihine kadar geçerli.`),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Katıl' })).toBeInTheDocument();
  });

  it('Open_ForAnotherAccount_ExplainsWithoutShowingAnyAddress', async () => {
    saveInvitationCode(code);
    resolves(false);
    const { authClient, container, onDone } = renderPage();

    expect(
      await screen.findByRole('heading', { name: 'This invitation is for another email address' }),
    ).toBeInTheDocument();
    expect(container.textContent).not.toContain('@');
    await expectAccessible(container);

    fireEvent.click(screen.getByRole('button', { name: 'Sign out and sign in with the right account' }));
    // Kept, so the invitation opens again once the right account has signed in.
    expect(authClient.signOut).toHaveBeenCalledOnce();
    expect(readInvitationCode()).toBe(code);

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(readInvitationCode()).toBeUndefined();
    expect(onDone).toHaveBeenCalledWith(undefined);
  });

  it('Join_AfterAnotherTabStoredANewerLink_KeepsTheNewerCode', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', { status: 202, body: { organizationId, status: 'used' } });
    const { onDone } = renderPage();
    const join = await screen.findByRole('button', { name: 'Join' });
    // Another tab opens a newer invitation link.
    const newer = 'ZYXwvu9876543210_-ZYXwvu9876543210_-ZYXwvu9';
    saveInvitationCode(newer);

    fireEvent.click(join);

    await waitFor(() => {
      expect(onDone).toHaveBeenCalledWith(organizationId);
    });
    expect(readInvitationCode()).toBe(newer);
  });

  it('Open_WithAnInvalidCode_ShowsTheInvalidLinkAndForgetsTheCode', async () => {
    saveInvitationCode(code);
    serve('post', '/api/v1/invitations/resolve', problem(404, 'directory.invitation_invalid'));
    const { container, onDone } = renderPage();

    expect(
      await screen.findByRole('heading', { name: "This invitation link isn't valid" }),
    ).toBeInTheDocument();
    expect(readInvitationCode()).toBeUndefined();
    await expectAccessible(container);
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(onDone).toHaveBeenCalledWith(undefined);
  });

  it('Open_WithoutACode_ShowsTheInvalidLinkWithoutCallingTheServer', async () => {
    renderPage();

    expect(
      await screen.findByRole('heading', { name: "This invitation link isn't valid" }),
    ).toBeInTheDocument();
    expect(seen).toHaveLength(0);
  });

  it('Open_WithAMalformedLinkWhileAnotherTabKeepsACode_ShowsInvalidAndKeepsTheCode', async () => {
    saveInvitationCode(code);
    const stored = localStorage.getItem(INVITATION_CODE_KEY);
    window.history.replaceState(null, '', '/invite#not-a-code');
    captureInvitationCode();

    renderPage();

    expect(
      await screen.findByRole('heading', { name: "This invitation link isn't valid" }),
    ).toBeInTheDocument();
    expect(seen).toHaveLength(0);
    expect(localStorage.getItem(INVITATION_CODE_KEY)).toBe(stored);
    leaveInvitePath();
  });

  it('Open_WhenRateLimited_SaysWhenToTryAgain', async () => {
    saveInvitationCode(code);
    serve(
      'post',
      '/api/v1/invitations/resolve',
      problem(429, 'directory.invitation_rate_limited', { 'Retry-After': '120' }),
      { body: { organizationName: 'Acme', expiresAt, matchesAccount: true } },
    );
    renderPage();

    expect(await screen.findByRole('heading', { name: 'Too many attempts' })).toBeInTheDocument();
    expect(screen.getByText('Try again in 2 minutes.')).toBeInTheDocument();
    expect(readInvitationCode()).toBe(code);

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Join Acme' })).toBeInTheDocument();
  });

  it('Join_WhenAccepted_PollsUntilUsedAndOpensTheOrganization', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', {
      status: 202,
      body: { organizationId, status: 'accepting' },
    });
    serve(
      'get',
      '/api/v1/invitations/acceptance',
      { body: { organizationId, status: 'accepting', code: null } },
      { body: { organizationId, status: 'accepting', code: null } },
      { body: { organizationId, status: 'used', code: null } },
    );
    const { onDone } = renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Joining the organization…');
    expect(readInvitationCode()).toBeUndefined();
    await waitFor(() => {
      expect(onDone).toHaveBeenCalledWith(organizationId);
    });
    expect(calls('/api/v1/invitations/acceptance')).toHaveLength(3);
    expect(calls('/api/v1/invitations/accept')[0]?.body).toBe(JSON.stringify({ code }));
    expect(sessionStorage.getItem(INVITATION_ACCEPTING_KEY)).toBeNull();
    // The code travels only in the two POST bodies, never in an address or a GET.
    for (const request of seen) {
      expect(request.url).not.toContain(code);
      expect(request.body.includes(code)).toBe(request.method === 'POST');
    }
  });

  it('Join_RepeatedAfterItWasUsed_OpensTheOrganizationAtOnce', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', { status: 202, body: { organizationId, status: 'used' } });
    const { onDone } = renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    await waitFor(() => {
      expect(onDone).toHaveBeenCalledWith(organizationId);
    });
    expect(calls('/api/v1/invitations/acceptance')).toHaveLength(0);
    expect(sessionStorage.length + localStorage.length).toBe(0);
  });

  it.each([
    ['organization.invitation_expired', 'The invitation has expired.'],
    ['organization.invitation_not_pending', 'The invitation was cancelled or has already been used.'],
    ['organization.member_deactivated', 'Your account in this organization has been deactivated.'],
    ['delivery_expired', "The organization's server couldn't add you."],
  ])('Join_WhenTheCellRefuses_ShowsTheReasonOnce (%s)', async (failure, reason) => {
    window.history.replaceState(null, '', '/invite');
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', {
      status: 202,
      body: { organizationId, status: 'accepting' },
    });
    serve('get', '/api/v1/invitations/acceptance', {
      body: { organizationId, status: 'failed', code: failure },
    });
    const { container, onDone } = renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    expect(
      await screen.findByRole('heading', { name: "You couldn't join the organization" }),
    ).toBeInTheDocument();
    expect(screen.getByText(reason)).toBeInTheDocument();
    expect(sessionStorage.length + localStorage.length).toBe(0);
    // A reload opens the app, not an invalid invitation.
    expect(window.location.pathname).toBe('/');
    await expectAccessible(container);
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(onDone).toHaveBeenCalledWith(undefined);
  });

  it('Join_WhenTheEmailIsNotVerified_SaysSoAndKeepsTheCode', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', problem(403, 'directory.email_not_verified'));
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Verify your email address to join.');
    expect(readInvitationCode()).toBe(code);
  });

  it('Join_WhenTheAddressDoesNotMatch_MovesFocusToTheExplanation', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', problem(403, 'directory.invitation_email_mismatch'));
    renderPage();

    const join = await screen.findByRole('button', { name: 'Join' });
    join.focus();
    fireEvent.click(join);

    const heading = await screen.findByRole('heading', {
      name: 'This invitation is for another email address',
    });
    await waitFor(() => {
      expect(heading).toHaveFocus();
    });
  });

  it('Open_AfterARateLimit_DoesNotResolveAgainInTheBackground', async () => {
    saveInvitationCode(code);
    serve(
      'post',
      '/api/v1/invitations/resolve',
      problem(429, 'directory.invitation_rate_limited', { 'Retry-After': '60' }),
    );
    renderPage();
    expect(await screen.findByRole('heading', { name: 'Too many attempts' })).toBeInTheDocument();

    focusManager.setFocused(false);
    focusManager.setFocused(true);
    onlineManager.setOnline(false);
    onlineManager.setOnline(true);
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(calls('/api/v1/invitations/resolve')).toHaveLength(1);
  });

  it('Open_WhenTheServerFails_DoesNotRetryOnItsOwn', async () => {
    saveInvitationCode(code);
    serve('post', '/api/v1/invitations/resolve', problem(503, 'http.503'));
    renderPage();

    expect(
      await screen.findByRole('heading', { name: 'Something went wrong. Try again.' }),
    ).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(calls('/api/v1/invitations/resolve')).toHaveLength(1);
  });

  it('Join_WhileWaiting_CanStopWaiting', async () => {
    sessionStorage.setItem(INVITATION_ACCEPTING_KEY, String(Date.now()));
    serve('get', '/api/v1/invitations/acceptance', {
      body: { organizationId, status: 'accepting', code: null },
    });
    const { onDone } = renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Continue' }));

    expect(onDone).toHaveBeenCalledWith(undefined);
    expect(sessionStorage.getItem(INVITATION_ACCEPTING_KEY)).toBeNull();
  });

  it('Join_WhenTheCodeIsNoLongerValid_ShowsTheInvalidLinkAndForgetsTheCode', async () => {
    saveInvitationCode(code);
    resolves();
    serve('post', '/api/v1/invitations/accept', problem(404, 'directory.invitation_invalid'));
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    expect(
      await screen.findByRole('heading', { name: "This invitation link isn't valid" }),
    ).toBeInTheDocument();
    expect(readInvitationCode()).toBeUndefined();
  });

  it('Join_WhenRateLimited_SaysWhenToTryAgain', async () => {
    saveInvitationCode(code);
    resolves();
    serve(
      'post',
      '/api/v1/invitations/accept',
      problem(429, 'directory.invitation_rate_limited', { 'Retry-After': '30' }),
    );
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Join' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many attempts. Try again in 1 minute.');
  });

  it('Join_WhenWaitingRunsOut_StopsPollingAndOffersToCheckAgain', async () => {
    sessionStorage.setItem(INVITATION_ACCEPTING_KEY, String(Date.now()));
    serve('get', '/api/v1/invitations/acceptance', {
      body: { organizationId, status: 'accepting', code: null },
    });
    const { container } = renderPage({ pollDelay: (polls) => (polls >= 2 ? false : 5) });

    expect(await screen.findByText(/Still joining/)).toBeInTheDocument();
    expect(calls('/api/v1/invitations/acceptance')).toHaveLength(2);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(calls('/api/v1/invitations/acceptance')).toHaveLength(2);
    await expectAccessible(container);

    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
    await waitFor(() => {
      expect(calls('/api/v1/invitations/acceptance')).toHaveLength(3);
    });
  });

  it('Join_WhileTheTabIsHidden_DoesNotPoll', async () => {
    sessionStorage.setItem(INVITATION_ACCEPTING_KEY, String(Date.now()));
    serve(
      'get',
      '/api/v1/invitations/acceptance',
      { body: { organizationId, status: 'accepting', code: null } },
      { body: { organizationId, status: 'used', code: null } },
    );
    focusManager.setFocused(false);
    const { onDone } = renderPage();

    await waitFor(() => {
      expect(calls('/api/v1/invitations/acceptance')).toHaveLength(1);
    });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(calls('/api/v1/invitations/acceptance')).toHaveLength(1);

    focusManager.setFocused(true);
    await waitFor(() => {
      expect(onDone).toHaveBeenCalledWith(organizationId);
    });
  });

  it('Reload_WhileJoining_KeepsWaitingWithoutTheCode', async () => {
    sessionStorage.setItem(INVITATION_ACCEPTING_KEY, String(Date.now()));
    serve('get', '/api/v1/invitations/acceptance', { body: { organizationId, status: 'used', code: null } });
    const { onDone } = renderPage();

    await waitFor(() => {
      expect(onDone).toHaveBeenCalledWith(organizationId);
    });
    expect(calls('/api/v1/invitations/resolve')).toHaveLength(0);
    expect(sessionStorage.getItem(INVITATION_ACCEPTING_KEY)).toBeNull();
  });
});

describe('acceptancePollDelay', () => {
  it('PollDelay_AfterEachAnswer_BacksOffAndStopsAfterAboutTwoMinutes', () => {
    const delays: number[] = [];
    for (let polls = 1; acceptancePollDelay(polls) !== false; polls += 1) {
      delays.push(acceptancePollDelay(polls) as number);
    }

    expect(delays.slice(0, 5)).toEqual([1000, 2000, 4000, 8000, 10_000]);
    expect(Math.max(...delays)).toBe(10_000);
    const total = delays.reduce((sum, delay) => sum + delay, 0);
    expect(total).toBeGreaterThanOrEqual(110_000);
    expect(total).toBeLessThanOrEqual(130_000);
  });
});
