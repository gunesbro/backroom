export type AuthUser = { name: string };

/** Sign-in for the operations panel; the OIDC implementation is in oidc-auth-client.ts, tests use a fake. */
export interface AuthClient {
  /** Finishes a sign-in redirect or returns the current user; otherwise leaves for the sign-in page. */
  start: () => Promise<AuthUser>;
  signIn: () => Promise<void>;
  /** A valid access token for the control plane, renewed when needed. */
  getAccessToken: () => Promise<string>;
  signOut: () => Promise<void>;
}
