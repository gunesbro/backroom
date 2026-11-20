import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createOidcAuthClient, returnPath, toAuthUser } from './oidc-auth-client';

const manager = vi.hoisted(() => ({
  signinRedirectCallback: vi.fn(),
  signinRedirect: vi.fn(),
  signinSilent: vi.fn(),
  getUser: vi.fn(),
  signoutRedirect: vi.fn(),
  clearStaleState: vi.fn(),
}));

const { ErrorResponse } = vi.hoisted(() => ({ ErrorResponse: class ErrorResponse extends Error {} }));

vi.mock('oidc-client-ts', () => ({
  ErrorResponse,
  UserManager: vi.fn(function UserManager() {
    return manager;
  }),
  WebStorageStateStore: vi.fn(),
  InMemoryWebStorage: vi.fn(),
}));

function user(overrides: Record<string, unknown> = {}) {
  return {
    access_token: 'token-1',
    expires_in: 300,
    expired: false,
    profile: { email: 'alice@example.test' },
    ...overrides,
  };
}

function client() {
  return createOidcAuthClient({ authority: 'https://auth.example.test/realms/test', clientId: 'web' });
}

beforeEach(() => {
  vi.resetAllMocks();
  manager.signinRedirect.mockResolvedValue(undefined);
  manager.clearStaleState.mockResolvedValue(undefined);
});

afterEach(() => {
  window.history.replaceState(null, '', '/');
  sessionStorage.clear();
});

describe('createOidcAuthClient', () => {
  it('Start_AfterTheSignInRedirect_ReturnsToTheSavedPathWithoutTheCode', async () => {
    window.history.replaceState(null, '', '/?code=secret-code&state=abc');
    manager.signinRedirectCallback.mockResolvedValue(user({ state: { path: '/invite' } }));

    const signedIn = await client().start();

    expect(signedIn).toEqual({
      email: 'alice@example.test',
      name: 'alice@example.test',
      givenName: 'alice@example.test',
    });
    expect(window.location.pathname).toBe('/invite');
    expect(window.location.search).toBe('');
  });

  it('Start_WhenTheRedirectFails_RemovesTheCodeAndRejects', async () => {
    window.history.replaceState(null, '', '/invite?error=access_denied&state=abc');
    manager.signinRedirectCallback.mockRejectedValue(new Error('access_denied'));

    await expect(client().start()).rejects.toThrow('access_denied');
    expect(window.location.pathname + window.location.search).toBe('/');
  });

  it('Start_WhenTheRedirectStateIsUnknown_SignsInAgainWithoutAnError', async () => {
    // Reproduces a sign-in finished from an email link in another tab: Keycloak returns where our state is missing.
    window.history.replaceState(null, '', '/?code=secret-code&state=abc');
    manager.signinRedirectCallback.mockRejectedValue(new Error('No matching state found in storage'));
    const outcome = vi.fn();

    client().start().then(outcome, outcome);

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
    expect(outcome).not.toHaveBeenCalled();
    expect(window.location.search).toBe('');
  });

  it('Start_WhenTheStateIsUnknownAgainRightAfterSigningInAgain_Fails', async () => {
    sessionStorage.setItem('ph.auth.unknown-state-retry-at', String(Date.now() - 5_000));
    window.history.replaceState(null, '', '/?code=secret-code&state=abc');
    manager.signinRedirectCallback.mockRejectedValue(new Error('No matching state found in storage'));

    await expect(client().start()).rejects.toThrow('No matching state');
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('Start_CalledTwice_HandlesTheRedirectOnce', async () => {
    window.history.replaceState(null, '', '/?code=secret-code&state=abc');
    manager.signinRedirectCallback.mockResolvedValue(user());
    const auth = client();

    await Promise.all([auth.start(), auth.start()]);

    expect(manager.signinRedirectCallback).toHaveBeenCalledOnce();
  });

  it('Start_WithoutASession_RedirectsToSignInReplacingThisPage', () => {
    manager.getUser.mockResolvedValue(null);
    window.history.replaceState(null, '', '/invite');

    void client().start();

    return vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledWith({
        state: { path: '/invite' },
        redirectMethod: 'replace',
      });
    });
  });

  it('GetAccessToken_WithTimeLeft_ReturnsTheCurrentToken', async () => {
    manager.getUser.mockResolvedValue(user());

    await expect(client().getAccessToken()).resolves.toBe('token-1');
    expect(manager.signinSilent).not.toHaveBeenCalled();
  });

  it('GetAccessToken_CloseToExpiry_RenewsOnceForConcurrentCallers', async () => {
    manager.getUser.mockResolvedValue(user({ expires_in: 10 }));
    manager.signinSilent.mockResolvedValue(user({ access_token: 'token-2' }));
    const auth = client();

    const tokens = await Promise.all([auth.getAccessToken(), auth.getAccessToken()]);

    expect(tokens).toEqual(['token-2', 'token-2']);
    expect(manager.signinSilent).toHaveBeenCalledOnce();
  });

  it('GetAccessToken_WhenTheSessionHasEnded_SignsInAgain', async () => {
    manager.getUser.mockResolvedValue(user({ expires_in: 0, expired: true }));
    manager.signinSilent.mockRejectedValue(new ErrorResponse('invalid_grant'));

    void client().getAccessToken();

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
  });

  it('GetAccessToken_WhenTheNetworkFails_RejectsWithoutLeaving', async () => {
    manager.getUser.mockResolvedValue(user({ expires_in: 0, expired: true }));
    manager.signinSilent.mockRejectedValue(new TypeError('Failed to fetch'));

    await expect(client().getAccessToken()).rejects.toThrow('Failed to fetch');
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('SignIn_CalledByManyCallers_RedirectsOnce', async () => {
    const auth = client();

    await Promise.all([auth.signIn(), auth.signIn()]);

    expect(manager.signinRedirect).toHaveBeenCalledOnce();
  });

  it('SignOut_Always_ReplacesThisPage', async () => {
    manager.signoutRedirect.mockResolvedValue(undefined);

    await client().signOut();

    expect(manager.signoutRedirect).toHaveBeenCalledWith({ redirectMethod: 'replace' });
  });
});

describe('returnPath', () => {
  it('Return_ToASameOriginPath_KeepsIt', () => {
    expect(returnPath({ path: '/invite' })).toBe('/invite');
  });

  it.each([
    undefined,
    null,
    'x',
    {},
    { path: 1 },
    { path: 'https://evil.example.test/' },
    { path: '//evil.example.test' },
    { path: '/\\evil.example.test' },
  ])('Return_ToAnythingElse_GoesHome (%j)', (state) => {
    expect(returnPath(state)).toBe('/');
  });
});

describe('toAuthUser', () => {
  it.each([
    [
      { email: 'a@example.test', name: 'Ali Kaya', given_name: 'Ali' },
      { name: 'Ali Kaya', givenName: 'Ali' },
    ],
    [
      { email: 'a@example.test', given_name: 'Ali', family_name: 'Kaya' },
      { name: 'Ali Kaya', givenName: 'Ali' },
    ],
    [
      { email: 'a@example.test', name: 'Ali Kaya' },
      { name: 'Ali Kaya', givenName: 'Ali Kaya' },
    ],
    [{ email: 'a@example.test' }, { name: 'a@example.test', givenName: 'a@example.test' }],
  ])('Describe_FromProfile_%j_UsesTheBestName', (profile, expected) => {
    expect(toAuthUser({ profile } as never)).toEqual({ email: 'a@example.test', ...expected });
  });
});
