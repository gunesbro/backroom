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
   * Feature flag: channels and messages (Phase 1 step 7), unfinished until live updates land. On in development
   * (.env.development), off in every other build unless VITE_MESSAGING_ENABLED=true.
   */
  messagingEnabled: import.meta.env.VITE_MESSAGING_ENABLED === 'true',
  /**
   * Feature flag: inviting people from the organization view (Phase 1 step 7). Needs messaging, because the cell is
   * reached through the same per-cell tokens, and the cell's own `Features:Invitations`. On in development.
   */
  invitationsUiEnabled: import.meta.env.VITE_INVITATIONS_UI_ENABLED === 'true',
  /**
   * Feature flag: the status menu and presence (Phase 2 step 28), with the servers' `Features:Presence`. Activity is
   * reported only to cells messaging connects to. On in development.
   */
  presenceEnabled: import.meta.env.VITE_PRESENCE_ENABLED === 'true',
  /**
   * Feature flag: the rich text composer (Phase 2 step 6): Lexical editor, formatting toolbar, blocks. Off: the plain
   * textarea. Off in development too (.env.development) until the server-side step 6 PR accepts client blocks.
   */
  richTextEnabled: import.meta.env.VITE_RICH_TEXT_ENABLED === 'true',
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
