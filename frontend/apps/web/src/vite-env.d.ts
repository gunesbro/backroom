/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_ENABLE_SPIKES?: string;
  readonly VITE_PRODUCT_NAME?: string;
  readonly VITE_AUTH_AUTHORITY?: string;
  readonly VITE_AUTH_CLIENT_ID?: string;
  readonly VITE_CONTROL_PLANE_URL?: string;
  readonly VITE_REGIONS?: string;
  readonly VITE_MESSAGING_ENABLED?: string;
  readonly VITE_INVITATIONS_UI_ENABLED?: string;
  readonly VITE_RICH_TEXT_ENABLED?: string;
  /** Set by vite.config.ts in tests when `CI` is set. */
  readonly BACKROOM_TEST_CI?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
