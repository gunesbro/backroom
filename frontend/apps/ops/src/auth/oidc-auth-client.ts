import {
  ErrorResponse,
  InMemoryWebStorage,
  UserManager,
  WebStorageStateStore,
  type User,
} from 'oidc-client-ts';
import type { AuthClient, AuthUser } from './auth-client';

type OidcSettings = { authority: string; clientId: string };

/** A token this close to expiry is renewed first, so it cannot expire on its way to the API. */
const RENEW_BEFORE_SECONDS = 30;

/**
 * Signs in with Keycloak (authorization code with PKCE) as the `ops` client, the way the web app does. Tokens
 * stay in memory only; a reload goes back through Keycloak, whose own session makes that a quick redirect.
 * Two-step verification is Keycloak's job; the control plane checks the token's `amr` claim.
 */
export function createOidcAuthClient({ authority, clientId }: OidcSettings): AuthClient {
  const appUrl = `${window.location.origin}/`;
  const manager = new UserManager({
    authority,
    client_id: clientId,
    redirect_uri: appUrl,
    post_logout_redirect_uri: appUrl,
    response_type: 'code',
    scope: 'openid profile control-plane',
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
    // Renewal happens on demand in getAccessToken: refresh tokens are single-use, so two renewals must never race.
    automaticSilentRenew: false,
    monitorSession: false,
    loadUserInfo: false,
  });

  let started: Promise<AuthUser> | undefined;
  let renewing: Promise<User | null> | undefined;
  let redirecting: Promise<void> | undefined;

  async function start(): Promise<AuthUser> {
    void manager.clearStaleState().catch(() => undefined);

    const query = new URLSearchParams(window.location.search);
    if ((query.has('code') || query.has('error')) && query.has('state')) {
      try {
        return toAuthUser(await manager.signinRedirectCallback());
      } finally {
        // The code and state never stay in the address bar or the history.
        window.history.replaceState(null, '', '/');
      }
    }

    const user = await manager.getUser();
    if (user && !user.expired) {
      return toAuthUser(user);
    }

    return leaveToSignIn();
  }

  function signIn(): Promise<void> {
    redirecting ??= manager.signinRedirect({ redirectMethod: 'replace' }).catch((error: unknown) => {
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

    renewing ??= manager.signinSilent().finally(() => {
      renewing = undefined;
    });
    let renewed: User | null;
    try {
      renewed = await renewing;
    } catch (error) {
      // Only the sign-in server's refusal ends the session; a network failure is an ordinary error.
      if (!(error instanceof ErrorResponse)) throw error;
      renewed = null;
    }

    return renewed?.access_token ?? leaveToSignIn();
  }

  return {
    // React's development mode runs effects twice; the redirect callback must run once.
    start: () => (started ??= start()),
    signIn,
    getAccessToken,
    signOut: () => manager.signoutRedirect({ redirectMethod: 'replace' }),
  };
}

function toAuthUser(user: Pick<User, 'profile'>): AuthUser {
  const { email = '', name, given_name: givenName, family_name: familyName } = user.profile;
  return { name: name?.trim() || [givenName, familyName].filter(Boolean).join(' ').trim() || email };
}
