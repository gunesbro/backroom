/** The signed-in account as the sign-in server describes it (OIDC profile claims). */
export type AuthUser = {
  email: string;
  /** The full name; the email when the account has none. */
  name: string;
  /** The first name for greetings; the full name when unknown. */
  givenName: string;
  /** The account id (the token's `sub`), when the sign-in client knows it; messages are attributed with it. */
  accountId?: string | undefined;
};

/**
 * Sign-in for the app; the OIDC implementation is in oidc-auth-client.ts, the desktop app's in
 * desktop-auth-client.ts, and tests use a fake.
 */
export interface AuthClient {
  /**
   * Finishes a sign-in redirect or returns the current user; otherwise leaves for the sign-in page (web) or
   * rejects with {@link SignInRequiredError} (desktop, which then offers the browser sign-in, {@link browserSignIn}).
   */
  start: () => Promise<AuthUser>;
  signIn: () => Promise<void>;
  /** A valid access token for our APIs, renewed when needed. */
  getAccessToken: () => Promise<string>;
  signOut: () => Promise<void>;
  /** Only the desktop app signs in through the system browser while the app waits (docs/design DesktopLogin). */
  browserSignIn?: BrowserSignIn | undefined;
}

export interface BrowserSignIn {
  /** Opens the sign-in page in the system browser and resolves once the app has its session. */
  run: (locale: string) => Promise<void>;
  /** Opens the waiting sign-in in the browser again. */
  reopen: () => Promise<void>;
  /** The waiting sign-in's link, to paste into a browser by hand. */
  link: () => Promise<string | null>;
  /** Stops waiting; `run` then rejects with `cancelled`. */
  cancel: () => Promise<void>;
}

/** No session yet: the desktop app shows its browser sign-in screen. */
export class SignInRequiredError extends Error {
  constructor() {
    super('Sign-in is required.');
    this.name = 'SignInRequiredError';
  }
}
