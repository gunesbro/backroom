// Build-time settings (Vite env). None of them is a secret: they are visible in the shipped bundle.

/** The AppHost's Keycloak realm; a production build must set VITE_AUTH_AUTHORITY. */
const DEVELOPMENT_AUTHORITY = 'https://localhost:8443/realms/backroom';

function required(name: string, value: string | undefined): string {
  if (!value) {
    throw new Error(`${name} is not set.`);
  }

  return value;
}

export const config = {
  /** The product name until branding is decided; the single setting the requirements call for. */
  productName: import.meta.env.VITE_PRODUCT_NAME ?? 'Backroom',
  auth: {
    /** The Keycloak realm, e.g. https://localhost:8443/realms/backroom. */
    authority: required(
      'VITE_AUTH_AUTHORITY',
      import.meta.env.VITE_AUTH_AUTHORITY ?? (import.meta.env.DEV ? DEVELOPMENT_AUTHORITY : undefined),
    ),
    clientId: import.meta.env.VITE_AUTH_CLIENT_ID ?? 'web',
  },
  /**
   * Feature flag, off by default: channels and messages (Phase 1 step 7). On, it still needs the cell token source,
   * which the per-cell token change provides; until then no cell is ever called. Set VITE_MESSAGING_ENABLED=true.
   */
  messagingEnabled: import.meta.env.VITE_MESSAGING_ENABLED === 'true',
  /** The control plane's origin; empty means this origin (the development server proxies /api). */
  controlPlaneUrl: import.meta.env.VITE_CONTROL_PLANE_URL ?? '',
  /** Data regions offered for new organizations, comma-separated region ids. */
  regions: required(
    'VITE_REGIONS',
    import.meta.env.VITE_REGIONS ?? (import.meta.env.DEV ? 'eu-dev' : undefined),
  )
    .split(',')
    .map((region) => region.trim())
    .filter(Boolean),
} as const;
