// Public surface of the auth feature: sign-in with Keycloak (web) or through the desktop shell, and the
// signed-in account.
export type { AuthClient, AuthUser } from './auth-client';
export { useAuth, type Auth } from './auth-context';
export { AuthProvider } from './auth-provider';
export { createDesktopAuthClient, isDesktopApp, tauriInvoke } from './desktop-auth-client';
export { createOidcAuthClient } from './oidc-auth-client';
