import {
  ErrorResponse,
  InMemoryWebStorage,
  UserManager,
  WebStorageStateStore,
  type User,
} from 'oidc-client-ts';
import { readItem, writeItem } from '@/shared/storage';
import type { AuthClient, AuthUser } from './auth-client';

type OidcSettings = { authority: string; clientId: string };

/** A token this close to expiry is renewed first, so it cannot expire on its way to the API. */
const RENEW_BEFORE_SECONDS = 30;

/**
 * Signs in with Keycloak (authorization code with PKCE). Tokens are kept in memory only, so they never reach
 * web storage; a reload goes back through Keycloak, whose own session makes that a quick redirect.
 */
export function createOidcAuthClient({ authority, clientId }: OidcSettings): AuthClient {
  const appUrl = `${window.location.origin}/`;
  const manager = new UserManager({
    authority,
    client_id: clientId,
    redirect_uri: appUrl,
    post_logout_redirect_uri: appUrl,
    response_type: 'code',
    scope: 'openid profile email control-plane',
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    // Only the PKCE verifier, nonce and return path, for the length of one redirect. Shared by the browser's tabs: a
    // link from an email (verify email, reset password) opens a new tab, which Keycloak returns to when it is done.
    stateStore: new WebStorageStateStore({ store: window.localStorage }),
    // Renewal happens on demand in getAccessToken: refresh tokens are single-use, so two renewals must never race.
    automaticSilentRenew: false,
    monitorSession: false,
    loadUserInfo: false,
  });

  let started: Promise<AuthUser> | undefined;
  let renewing: Promise<User | null> | undefined;
  let redirecting: Promise<void> | undefined;

  async function start(): Promise<AuthUser> {
    // Redirects that were never finished (a closed tab) leave state behind; it is dropped after 15 minutes.
    void manager.clearStaleState().catch(() => undefined);

    const query = new URLSearchParams(window.location.search);
    if ((query.has('code') || query.has('error')) && query.has('state')) {
      let path = '/';
      try {
        const user = await manager.signinRedirectCallback();
        path = returnPath(user.state);
        return toAuthUser(user);
      } catch (error) {
        // Keycloak came back to a browser without our redirect state (it was cleared, or the sign-in began in another
        // browser). Its own session is usually still there, so signing in again is quick; once, so it cannot loop.
        if (isUnknownState(error) && !recentlyRetried()) {
          writeItem('session', UNKNOWN_STATE_RETRY_KEY, String(Date.now()));
          // Leaving never settles, so the finally below would not run: drop the code first.
          window.history.replaceState(null, '', path);
          return await leaveToSignIn();
        }

        throw error;
      } finally {
        // The code and state never stay in the address bar or the history.
        window.history.replaceState(null, '', path);
      }
    }

    const user = await manager.getUser();
    if (user && !user.expired) {
      return toAuthUser(user);
    }

    return leaveToSignIn();
  }

  function signIn(): Promise<void> {
    // One redirect, however many callers need it. Replaced, not pushed: going back must not return to a page
    // waiting for a redirect that already happened.
    redirecting ??= manager
      .signinRedirect({ state: { path: window.location.pathname }, redirectMethod: 'replace' })
      .catch((error: unknown) => {
        redirecting = undefined;
        throw error;
      });
    return redirecting;
  }

  /** The browser is leaving for the sign-in page, so whoever waits never continues on this page. */
  async function leaveToSignIn(): Promise<never> {
    await signIn();
    return new Promise<never>(() => undefined);
  }

  async function getAccessToken(): Promise<string> {
    const user = await manager.getUser();
    if (user?.access_token && (user.expires_in ?? 0) > RENEW_BEFORE_SECONDS) {
      return user.access_token;
    }

    // One refresh at a time, however many requests need a token.
    renewing ??= manager.signinSilent().finally(() => {
      renewing = undefined;
    });
    let renewed: User | null;
    try {
      renewed = await renewing;
    } catch (error) {
      // Only the sign-in server's refusal ends the session; a network failure (a laptop waking before its
      // connection) is an ordinary error the request can retry.
      if (!(error instanceof ErrorResponse)) throw error;
      renewed = null;
    }

    // The session has ended (expired, signed out elsewhere or revoked): sign in again. What the person was
    // doing survives in session storage (a pending organization) or local storage (the last organization).
    return renewed?.access_token ?? leaveToSignIn();
  }

  return {
    // React's development mode runs effects twice; the redirect callback must run once.
    start: () => (started ??= start()),
    signIn,
    getAccessToken,
    // Replaced, so going back cannot show the signed-in page again (shared devices).
    signOut: () => manager.signoutRedirect({ redirectMethod: 'replace' }),
  };
}

export function toAuthUser(user: Pick<User, 'profile'>): AuthUser {
  const { email = '', name, given_name: givenName, family_name: familyName } = user.profile;
  const fullName = name?.trim() || [givenName, familyName].filter(Boolean).join(' ').trim() || email;
  return { email, name: fullName, givenName: givenName?.trim() || fullName };
}

/** When a redirect without known state last started a new sign-in; a second one soon after is shown as a failure. */
const UNKNOWN_STATE_RETRY_KEY = 'ph.auth.unknown-state-retry-at';
const UNKNOWN_STATE_RETRY_PAUSE_MS = 60_000;

/** oidc-client-ts 3.5.0 has no error type for this; its message is checked by unknown-state.test.ts. */
export function isUnknownState(error: unknown): boolean {
  return error instanceof Error && error.message.includes('No matching state');
}

function recentlyRetried(): boolean {
  return Date.now() - Number(readItem('session', UNKNOWN_STATE_RETRY_KEY)) < UNKNOWN_STATE_RETRY_PAUSE_MS;
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
