import {
  ErrorResponse,
  InMemoryWebStorage,
  UserManager,
  WebStorageStateStore,
  type User,
} from 'oidc-client-ts';
import { readItem, writeItem } from '@/shared/storage';
import type { AuthClient, AuthUser } from './auth-client';
import {
  CellUnavailableError,
  CONTROL_PLANE,
  InvalidScopeError,
  ScopeNotGrantedError,
  SessionEndedError,
  cellOf,
  fetchSharedCells,
  isCellId,
  signInCells,
  targetsOf,
  type TokenTarget,
} from './cell-tokens';
import { createRememberedCells } from './remembered-cells';
import { createRefresh, createTokenManager, type TokenManager } from './token-manager';

type OidcSettings = {
  authority: string;
  clientId: string;
  /** The control plane's origin, for the shared cells' list; empty means this origin. */
  controlPlaneUrl: string;
  fetch?: typeof fetch;
  now?: () => number;
};

/** Scopes of every sign-in besides the cells' (contract, "Sign-in scopes"); the profile names the account. */
const BASE_SCOPES = ['openid', 'profile', 'email', CONTROL_PLANE];
/** The shared and remembered cells and the one being added; anything larger is not ours. */
const MAX_UNAVAILABLE_CELLS = 32;

/** What a sign-in redirect carries to the page it returns to; only the path and cell ids, never a token. */
type RedirectState = {
  path: string;
  /** The cell this redirect authorizes. */
  cell?: string;
  /** Cells already found unavailable in this sign-in, so authorizing another one cannot retry them. */
  unavailable?: string[];
  prompt?: 'none';
  /** The sign-in without the remembered cells after `invalid_scope`; it is not retried again. */
  withoutRemembered?: true;
};

type SignInOptions = Omit<RedirectState, 'path'> & { path?: string };

/**
 * Signs in with Keycloak (authorization code with PKCE). Tokens are kept in memory only, so they never reach web
 * storage; a reload goes back through Keycloak, whose own session makes that a quick redirect. Each access token
 * names one target (contract, "Audience per target").
 */
export function createOidcAuthClient({
  authority,
  clientId,
  controlPlaneUrl,
  fetch: fetchFn = (input, init) => fetch(input, init),
  now = Date.now,
}: OidcSettings): AuthClient {
  const appUrl = `${window.location.origin}/`;
  const manager = new UserManager({
    authority,
    client_id: clientId,
    redirect_uri: appUrl,
    post_logout_redirect_uri: appUrl,
    response_type: 'code',
    scope: BASE_SCOPES.join(' '),
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    // Only the PKCE verifier, nonce and return state, for the length of one redirect. Shared by the browser's tabs: a
    // link from an email (verify email, reset password) opens a new tab, which Keycloak returns to when it is done.
    stateStore: new WebStorageStateStore({ store: window.localStorage }),
    // Tokens are renewed by the token manager, one refresh at a time.
    automaticSilentRenew: false,
    monitorSession: false,
    loadUserInfo: false,
  });
  const refresh = createRefresh({
    tokenEndpoint: () => manager.metadataService.getTokenEndpoint(false),
    clientId,
    fetch: fetchFn,
  });
  const remembered = createRememberedCells(now);

  let started: Promise<AuthUser> | undefined;
  let redirecting: Promise<void> | undefined;
  let tokens: TokenManager | undefined;
  let idToken: string | undefined;
  /** Cells whose scope is still missing after authorizing them; not tried again until the next sign-in. */
  let unavailable = new Set<string>();

  async function start(): Promise<AuthUser> {
    // Redirects that were never finished (a closed tab) leave state behind; it is dropped after 15 minutes.
    void manager.clearStaleState().catch(() => undefined);

    const query = new URLSearchParams(window.location.search);
    if ((query.has('code') || query.has('error')) && query.has('state')) {
      let path = '/';
      try {
        const user = await manager.signinRedirectCallback();
        const state = redirectState(user.state);
        path = state.path;
        await adopt(user, state);
        return toAuthUser(user);
      } catch (error) {
        // Keycloak came back to a browser without our redirect state (it was cleared, or the sign-in began in another
        // browser). Its own session is usually still there, so signing in again is quick; once, so it cannot loop.
        if (isUnknownState(error) && !recently(UNKNOWN_STATE_RETRY_KEY)) {
          writeItem('session', UNKNOWN_STATE_RETRY_KEY, String(now()));
          // Leaving never settles, so the finally below would not run: drop the code first.
          window.history.replaceState(null, '', path);
          return await leaveToSignIn();
        }

        const fallback = error instanceof ErrorResponse ? fallbackFor(error) : undefined;
        if (fallback) {
          path = fallback.path ?? '/';
          window.history.replaceState(null, '', path);
          return await leaveToSignIn(fallback);
        }

        throw error;
      } finally {
        // The code and state never stay in the address bar or the history.
        window.history.replaceState(null, '', path);
      }
    }

    return leaveToSignIn();
  }

  /** The contract's fallbacks for a cell's authorization ("A cell whose scope was not granted"). */
  function fallbackFor(error: ErrorResponse): SignInOptions | undefined {
    const state = redirectState(error.state);
    // The Keycloak session needs the person (it ended, or a step is due): the normal sign-in, with the cell.
    if (
      (error.error === 'login_required' || error.error === 'interaction_required') &&
      state.prompt === 'none'
    ) {
      const fallback: SignInOptions = { ...state };
      delete fallback.prompt;
      return fallback;
    }

    // A remembered cell no longer exists: once more without the remembered cells, and forget them. The cell being
    // authorized is left out too, as Keycloak does not say which scope it refused; it then shows as unavailable.
    if (error.error === 'invalid_scope' && !state.withoutRemembered) {
      remembered.forget();
      return { ...state, withoutRemembered: true };
    }

    return undefined;
  }

  /** Takes over a finished sign-in: keeps its refresh token and drops the code exchange's access token unused. */
  async function adopt(user: User, state: RedirectState) {
    if (!user.refresh_token) throw new Error('The sign-in returned no refresh token.');
    const granted = targetsOf(user.scope);
    tokens?.clear();
    tokens = createTokenManager({ refreshToken: user.refresh_token, granted, refresh, now });
    idToken = user.id_token;
    // The code exchange's access token carries every granted audience (contract, "One audience per token").
    await manager.removeUser();

    unavailable = new Set(state.unavailable);
    if (state.cell && !granted.has(`cell:${state.cell}`)) unavailable.add(state.cell);
    // At once, so the token that carries every audience is never the newest one.
    void tokens.get(CONTROL_PLANE).catch(() => undefined);
  }

  function signIn(options: SignInOptions = {}): Promise<void> {
    // One redirect, however many callers need it. Replaced, not pushed: going back must not return to a page
    // waiting for a redirect that already happened.
    redirecting ??= (async () => {
      const shared = await fetchSharedCells(controlPlaneUrl, fetchFn);
      const { cell, prompt, withoutRemembered } = options;
      if (cell !== undefined && !withoutRemembered && !shared.includes(cell)) remembered.remember(cell);
      const cells = withoutRemembered ? shared : signInCells(shared, remembered.list(), cell);
      const state: RedirectState = {
        path: options.path ?? currentPath(),
        ...(cell !== undefined && { cell }),
        ...(options.unavailable?.length && {
          unavailable: options.unavailable.slice(0, MAX_UNAVAILABLE_CELLS),
        }),
        ...(prompt && { prompt }),
        ...(withoutRemembered && { withoutRemembered }),
      };
      await manager.signinRedirect({
        scope: [...BASE_SCOPES, ...cells.map((id) => `cell:${id}`)].join(' '),
        state,
        redirectMethod: 'replace',
        ...(prompt && { prompt }),
      });
    })().catch((error: unknown) => {
      redirecting = undefined;
      throw error;
    });
    return redirecting;
  }

  /** The browser is leaving for the sign-in page, so whoever waits never continues on this page. */
  async function leaveToSignIn(options?: SignInOptions): Promise<never> {
    await signIn(options);
    return new Promise<never>(() => undefined);
  }

  async function getAccessToken(target: TokenTarget): Promise<string> {
    const cellId = cellOf(target);
    if (cellId !== undefined && unavailable.has(cellId)) throw new CellUnavailableError(cellId);
    if (!tokens) return leaveToSignIn();

    try {
      const token = await tokens.get(target);
      if (cellId !== undefined) remembered.touch(cellId);
      return token;
    } catch (error) {
      // The refresh token was refused (used up by another request in a race, or the session ended): new tokens
      // through Keycloak's session, as on reload. Once a minute at most, so a fault cannot loop.
      if (error instanceof SessionEndedError) {
        if (recently(SESSION_RECOVERY_KEY)) throw error;
        writeItem('session', SESSION_RECOVERY_KEY, String(now()));
        return leaveToSignIn();
      }

      if (cellId !== undefined && isCellId(cellId)) {
        // Not in this sign-in: authorize it through Keycloak's session, without a password.
        if (error instanceof ScopeNotGrantedError && !error.afterRefresh) {
          return leaveToSignIn({ cell: cellId, prompt: 'none', unavailable: [...unavailable] });
        }

        // Granted at sign-in but left out now, or no longer known: asking again would not help.
        if (error instanceof ScopeNotGrantedError || error instanceof InvalidScopeError) {
          if (unavailable.size < MAX_UNAVAILABLE_CELLS) unavailable.add(cellId);
          throw new CellUnavailableError(cellId);
        }
      }

      throw error;
    }
  }

  return {
    // React's development mode runs effects twice; the redirect callback must run once.
    start: () => (started ??= start()),
    signIn: () => signIn(),
    getAccessToken,
    signOut: async () => {
      tokens?.clear();
      tokens = undefined;
      unavailable = new Set();
      remembered.forget();
      // Replaced, so going back cannot show the signed-in page again (shared devices).
      await manager.signoutRedirect({
        redirectMethod: 'replace',
        ...(idToken && { id_token_hint: idToken }),
      });
    },
  };

  function recently(key: string): boolean {
    return now() - Number(readItem('session', key)) < RETRY_PAUSE_MS;
  }
}

export function toAuthUser(user: Pick<User, 'profile'>): AuthUser {
  const { email = '', name, given_name: givenName, family_name: familyName, sub } = user.profile;
  const fullName = name?.trim() || [givenName, familyName].filter(Boolean).join(' ').trim() || email;
  return {
    email,
    name: fullName,
    givenName: givenName?.trim() || fullName,
    ...(sub ? { accountId: sub } : {}),
  };
}

/** When a redirect without known state last started a new sign-in; a second one soon after is shown as a failure. */
const UNKNOWN_STATE_RETRY_KEY = 'ph.auth.unknown-state-retry-at';
/** When a refused refresh token last sent the tab through Keycloak's session for new tokens. */
const SESSION_RECOVERY_KEY = 'ph.auth.session-recovery-at';
const RETRY_PAUSE_MS = 60_000;

/** oidc-client-ts 3.5.0 has no error type for this; its message is checked by unknown-state.test.ts. */
export function isUnknownState(error: unknown): boolean {
  return error instanceof Error && error.message.includes('No matching state');
}

/** The page to return to, with its query and fragment. */
function currentPath(): string {
  const { pathname, search, hash } = window.location;
  return `${pathname}${search}${hash}`;
}

/** Only a same-origin path is accepted, so the state can never send the browser elsewhere. */
export function returnPath(state: unknown): string {
  if (typeof state === 'object' && state !== null && 'path' in state && typeof state.path === 'string') {
    if (state.path.startsWith('/') && !state.path.startsWith('//') && !state.path.includes('\\')) {
      return state.path;
    }
  }

  return '/';
}

/** The redirect state, checked: it comes back through local storage, so nothing in it is trusted. */
function redirectState(state: unknown): RedirectState {
  const result: RedirectState = { path: returnPath(state) };
  if (typeof state !== 'object' || state === null) return result;
  const raw = state as Record<string, unknown>;
  if (typeof raw.cell === 'string' && isCellId(raw.cell)) result.cell = raw.cell;
  if (Array.isArray(raw.unavailable)) {
    result.unavailable = raw.unavailable
      .filter((id): id is string => typeof id === 'string' && isCellId(id))
      .slice(0, MAX_UNAVAILABLE_CELLS);
  }
  if (raw.prompt === 'none') result.prompt = 'none';
  if (raw.withoutRemembered === true) result.withoutRemembered = true;
  return result;
}
