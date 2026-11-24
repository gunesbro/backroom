import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CellUnavailableError, SessionEndedError } from './cell-tokens';
import { createOidcAuthClient, returnPath, toAuthUser } from './oidc-auth-client';

const manager = vi.hoisted(() => ({
  signinRedirectCallback: vi.fn(),
  signinRedirect: vi.fn(),
  signoutRedirect: vi.fn(),
  clearStaleState: vi.fn(),
  removeUser: vi.fn(),
  metadataService: { getTokenEndpoint: vi.fn() },
}));

const { ErrorResponse } = vi.hoisted(() => ({
  ErrorResponse: class ErrorResponse extends Error {
    constructor(
      readonly error: string,
      readonly state?: unknown,
    ) {
      super(error);
    }
  },
}));

vi.mock('oidc-client-ts', () => ({
  ErrorResponse,
  UserManager: vi.fn(function UserManager() {
    return manager;
  }),
  WebStorageStateStore: vi.fn(),
  InMemoryWebStorage: vi.fn(),
}));

const TOKEN_ENDPOINT = 'https://auth.example.test/realms/test/protocol/openid-connect/token';
const REMEMBERED_KEY = 'ph.auth.remembered-cells';

/**
 * Keycloak's token endpoint and the control plane's shared-cell list. Refresh tokens are single-use and a refresh
 * answers only with a scope granted at sign-in.
 */
function endpoints({ granted = ['control-plane'], shared = [] as string[] } = {}) {
  let valid = 'refresh-0';
  let issued = 0;
  const refreshes: { scope: string; refreshToken: string }[] = [];
  let failNext: string | undefined;
  const fetch = vi.fn<typeof globalThis.fetch>((input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    if (url.endsWith('/api/v1/cells/shared')) {
      return Promise.resolve(Response.json({ cells: shared.map((id) => ({ id })) }));
    }
    const form = new URLSearchParams(init?.body as URLSearchParams);
    const scope = form.get('scope') ?? '';
    const refreshToken = form.get('refresh_token') ?? '';
    refreshes.push({ scope, refreshToken });
    if (failNext) {
      const error = failNext;
      failNext = undefined;
      return Promise.resolve(Response.json({ error }, { status: 400 }));
    }
    if (refreshToken !== valid)
      return Promise.resolve(Response.json({ error: 'invalid_grant' }, { status: 400 }));
    issued += 1;
    valid = `refresh-${String(issued)}`;
    return Promise.resolve(
      Response.json({
        access_token: `access-${scope}-${String(issued)}`,
        refresh_token: valid,
        expires_in: 300,
        scope: granted.includes(scope) ? scope : '',
      }),
    );
  });
  return {
    fetch,
    refreshes,
    failNextWith: (error: string) => {
      failNext = error;
    },
  };
}

function user(overrides: Record<string, unknown> = {}) {
  return {
    access_token: 'all-audiences',
    refresh_token: 'refresh-0',
    id_token: 'id-token',
    scope: 'openid profile email control-plane',
    expires_in: 300,
    expired: false,
    profile: { email: 'alice@example.test' },
    ...overrides,
  };
}

/** The clock of the client under test; tests move it forward. */
let time = Date.now();

function client(fetch: typeof globalThis.fetch = endpoints().fetch) {
  return createOidcAuthClient({
    authority: 'https://auth.example.test/realms/test',
    clientId: 'web',
    controlPlaneUrl: 'https://cp.example.test',
    fetch,
    now: () => time,
  });
}

/** Signs in through a redirect callback that granted `scope`. */
async function signedIn(fetch: typeof globalThis.fetch, overrides: Record<string, unknown> = {}) {
  window.history.replaceState(null, '', '/?code=secret-code&state=abc');
  manager.signinRedirectCallback.mockResolvedValue(user(overrides));
  const auth = client(fetch);
  await auth.start();
  return auth;
}

function redirectArgs(call = 0) {
  return manager.signinRedirect.mock.calls[call]?.[0] as {
    scope: string;
    state: Record<string, unknown>;
    prompt?: string;
  };
}

beforeEach(() => {
  vi.resetAllMocks();
  time = Date.now();
  manager.signinRedirect.mockResolvedValue(undefined);
  manager.clearStaleState.mockResolvedValue(undefined);
  manager.removeUser.mockResolvedValue(undefined);
  manager.signoutRedirect.mockResolvedValue(undefined);
  manager.metadataService.getTokenEndpoint.mockResolvedValue(TOKEN_ENDPOINT);
});

afterEach(() => {
  window.history.replaceState(null, '', '/');
  sessionStorage.clear();
  localStorage.clear();
});

describe('createOidcAuthClient', () => {
  it('Start_AfterTheSignInRedirect_ReturnsToTheSavedPathWithoutTheCode', async () => {
    window.history.replaceState(null, '', '/?code=secret-code&state=abc');
    manager.signinRedirectCallback.mockResolvedValue(user({ state: { path: '/invite' } }));

    const signedInUser = await client().start();

    expect(signedInUser).toEqual({
      email: 'alice@example.test',
      name: 'alice@example.test',
      givenName: 'alice@example.test',
    });
    expect(window.location.pathname).toBe('/invite');
    expect(window.location.search).toBe('');
  });

  it('Start_AfterTheCodeExchange_DiscardsItsTokenAndNarrowsToTheControlPlaneAtOnce', async () => {
    const keycloak = endpoints({ granted: ['control-plane', 'cell:eu-1'] });

    const auth = await signedIn(keycloak.fetch, { scope: 'openid control-plane cell:eu-1' });

    expect(manager.removeUser).toHaveBeenCalledOnce();
    await vi.waitFor(() => {
      expect(keycloak.refreshes).toEqual([{ scope: 'control-plane', refreshToken: 'refresh-0' }]);
    });
    const token = await auth.getAccessToken('control-plane');
    expect(token).toBe('access-control-plane-1');
    expect(token).not.toBe('all-audiences');
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

  it('Start_WithoutASession_RedirectsWithTheSharedAndRememberedCellsReplacingThisPage', async () => {
    localStorage.setItem(REMEMBERED_KEY, JSON.stringify({ 'own-1': Date.now() - 1_000 }));
    window.history.replaceState(null, '', '/invite?tab=2#top');

    void client(endpoints({ shared: ['eu-1', 'us-1'] }).fetch).start();

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledWith({
        scope: 'openid profile email control-plane cell:eu-1 cell:us-1 cell:own-1',
        state: { path: '/invite?tab=2#top' },
        redirectMethod: 'replace',
      });
    });
  });

  it('GetAccessToken_ForAGrantedCell_CarriesOnlyThatCellsScope', async () => {
    const keycloak = endpoints({ granted: ['control-plane', 'cell:eu-1'] });
    const auth = await signedIn(keycloak.fetch, { scope: 'openid control-plane cell:eu-1' });

    await expect(auth.getAccessToken('cell:eu-1')).resolves.toMatch(/^access-cell:eu-1-/);
    expect(keycloak.refreshes.map((refresh) => refresh.scope)).toEqual(['control-plane', 'cell:eu-1']);
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('GetAccessToken_ForACellNotGranted_AuthorizesItWithoutAPasswordAndRemembersIt', async () => {
    const keycloak = endpoints({ shared: ['eu-1'] });
    const auth = await signedIn(keycloak.fetch, { state: { path: '/orgs/1' } });
    const outcome = vi.fn();

    auth.getAccessToken('cell:own-1').then(outcome, outcome);

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
    expect(redirectArgs()).toEqual({
      scope: 'openid profile email control-plane cell:eu-1 cell:own-1',
      state: { path: '/orgs/1', cell: 'own-1', prompt: 'none' },
      redirectMethod: 'replace',
      prompt: 'none',
    });
    expect(Object.keys(JSON.parse(localStorage.getItem(REMEMBERED_KEY) ?? '{}') as object)).toEqual([
      'own-1',
    ]);
    expect(outcome).not.toHaveBeenCalled();
  });

  it('Start_AfterAuthorizingACellThatIsStillMissing_ShowsItUnavailableWithoutRedirectingAgain', async () => {
    const keycloak = endpoints();
    const auth = await signedIn(keycloak.fetch, { state: { path: '/', cell: 'own-1', prompt: 'none' } });

    await expect(auth.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellUnavailableError);
    await expect(auth.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellUnavailableError);
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('GetAccessToken_ForAnotherCellAfterOneWasUnavailable_CarriesThatOneSoItIsNotRetried', async () => {
    const keycloak = endpoints();
    const auth = await signedIn(keycloak.fetch, { state: { path: '/', cell: 'own-1', prompt: 'none' } });

    void auth.getAccessToken('cell:own-2');

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
    expect(redirectArgs().state).toEqual({
      path: '/',
      cell: 'own-2',
      unavailable: ['own-1'],
      prompt: 'none',
    });
  });

  it('GetAccessToken_WhenKeycloakLeavesOutAGrantedCell_ShowsItUnavailableWithoutRedirecting', async () => {
    const keycloak = endpoints({ granted: ['control-plane'] });
    const auth = await signedIn(keycloak.fetch, { scope: 'openid control-plane cell:gone' });

    await expect(auth.getAccessToken('cell:gone')).rejects.toBeInstanceOf(CellUnavailableError);
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it.each(['login_required', 'interaction_required'])(
    'Start_WhenTheSilentAuthorizationAnswers_%s_FallsBackToTheNormalSignInWithTheCell',
    async (error) => {
      window.history.replaceState(null, '', '/?error=x&state=abc');
      manager.signinRedirectCallback.mockRejectedValue(
        new ErrorResponse(error, { path: '/orgs/1', cell: 'own-1', prompt: 'none' }),
      );

      void client().start();

      await vi.waitFor(() => {
        expect(manager.signinRedirect).toHaveBeenCalledOnce();
      });
      expect(redirectArgs()).toEqual({
        scope: 'openid profile email control-plane cell:own-1',
        state: { path: '/orgs/1', cell: 'own-1' },
        redirectMethod: 'replace',
      });
    },
  );

  it('Start_WhenANormalSignInAnswersLoginRequired_RejectsInsteadOfLooping', async () => {
    window.history.replaceState(null, '', '/?error=x&state=abc');
    manager.signinRedirectCallback.mockRejectedValue(new ErrorResponse('login_required', { path: '/' }));

    await expect(client().start()).rejects.toThrow('login_required');
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('Start_WhenAScopeIsUnknown_ForgetsTheRememberedCellsAndRetriesOnceWithoutThem', async () => {
    localStorage.setItem(REMEMBERED_KEY, JSON.stringify({ gone: Date.now() - 1_000 }));
    window.history.replaceState(null, '', '/?error=invalid_scope&state=abc');
    manager.signinRedirectCallback.mockRejectedValue(
      new ErrorResponse('invalid_scope', { path: '/orgs/1', cell: 'gone', prompt: 'none' }),
    );

    void client(endpoints({ shared: ['eu-1'] }).fetch).start();

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
    expect(localStorage.getItem(REMEMBERED_KEY)).toBeNull();
    expect(redirectArgs()).toEqual({
      scope: 'openid profile email control-plane cell:eu-1',
      state: { path: '/orgs/1', cell: 'gone', prompt: 'none', withoutRemembered: true },
      redirectMethod: 'replace',
      prompt: 'none',
    });
  });

  it('Start_WhenAScopeIsUnknownAgainAfterTheRetry_Rejects', async () => {
    window.history.replaceState(null, '', '/?error=invalid_scope&state=abc');
    manager.signinRedirectCallback.mockRejectedValue(
      new ErrorResponse('invalid_scope', { path: '/', withoutRemembered: true }),
    );

    await expect(client().start()).rejects.toThrow('invalid_scope');
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('GetAccessToken_WhenTheRefreshTokenIsRefused_GetsNewTokensThroughTheSessionOnce', async () => {
    const keycloak = endpoints();
    const auth = await signedIn(keycloak.fetch);
    await auth.getAccessToken('control-plane');
    keycloak.failNextWith('invalid_grant');
    time += 10 * 60_000;

    void auth.getAccessToken('control-plane');

    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
    expect(redirectArgs().prompt).toBeUndefined();
    expect(sessionStorage.getItem('ph.auth.session-recovery-at')).toBe(String(time));
  });

  it('GetAccessToken_WhenRefusedAgainRightAfterARecovery_RejectsInsteadOfLooping', async () => {
    sessionStorage.setItem('ph.auth.session-recovery-at', String(time - 5_000));
    const keycloak = endpoints();
    keycloak.failNextWith('invalid_grant');
    const auth = await signedIn(keycloak.fetch);

    await expect(auth.getAccessToken('control-plane')).rejects.toBeInstanceOf(SessionEndedError);
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('GetAccessToken_WhenTheNetworkFails_RejectsWithoutLeaving', async () => {
    const keycloak = endpoints();
    const auth = await signedIn(keycloak.fetch);
    await auth.getAccessToken('control-plane');
    keycloak.fetch.mockRejectedValue(new TypeError('Failed to fetch'));
    time += 10 * 60_000;

    await expect(auth.getAccessToken('control-plane')).rejects.toThrow('Failed to fetch');
    expect(manager.signinRedirect).not.toHaveBeenCalled();
  });

  it('SignIn_CalledByManyCallers_RedirectsOnce', async () => {
    const auth = client();

    await Promise.all([auth.signIn(), auth.signIn()]);

    expect(manager.signinRedirect).toHaveBeenCalledOnce();
  });

  it('SignOut_ForgetsTokensAndRememberedCellsAndReplacesThisPage', async () => {
    const keycloak = endpoints();
    const auth = await signedIn(keycloak.fetch);
    await auth.getAccessToken('control-plane');
    localStorage.setItem(REMEMBERED_KEY, JSON.stringify({ 'own-1': Date.now() - 1_000 }));
    const refreshes = keycloak.refreshes.length;

    await auth.signOut();

    expect(manager.signoutRedirect).toHaveBeenCalledWith({
      redirectMethod: 'replace',
      id_token_hint: 'id-token',
    });
    expect(localStorage.getItem(REMEMBERED_KEY)).toBeNull();
    void auth.getAccessToken('control-plane');
    await vi.waitFor(() => {
      expect(manager.signinRedirect).toHaveBeenCalledOnce();
    });
    expect(keycloak.refreshes).toHaveLength(refreshes);
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
