// Public surface of the auth feature: sign-in with Keycloak (web) or through the desktop shell, the signed-in
// account and its tokens, one target each.
export type { AuthClient, AuthUser } from './auth-client';
export { useAuth, type Auth } from './auth-context';
export { AuthProvider } from './auth-provider';
export { CellSignInPrompt } from './cell-sign-in-prompt';
export { CellSignInPendingError, CellUnavailableError, type TokenTarget } from './cell-tokens';
export { createDesktopAuthClient, isDesktopApp, tauriInvoke } from './desktop-auth-client';
export { createOidcAuthClient } from './oidc-auth-client';
