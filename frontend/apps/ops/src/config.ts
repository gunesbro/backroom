// Build-time settings (Vite env). None of them is a secret: they are visible in the shipped bundle.

/** The AppHost's Keycloak realm; a production build must set VITE_AUTH_AUTHORITY. */
const DEVELOPMENT_AUTHORITY = 'https://localhost:8443/realms/backroom';

function authority(): string {
  const value =
    import.meta.env.VITE_AUTH_AUTHORITY ?? (import.meta.env.DEV ? DEVELOPMENT_AUTHORITY : undefined);
  if (!value) {
    throw new Error('VITE_AUTH_AUTHORITY is not set.');
  }

  return value;
}

export const config = {
  auth: {
    /** The Keycloak realm, e.g. https://localhost:8443/realms/backroom. */
    authority,
    /** The operations panel has its own client; the control plane accepts the ops role only from it. */
    clientId: import.meta.env.VITE_AUTH_CLIENT_ID ?? 'ops',
  },
  /** The control plane's origin; empty means this origin (the development server proxies /api). */
  controlPlaneUrl: import.meta.env.VITE_CONTROL_PLANE_URL ?? '',
};
