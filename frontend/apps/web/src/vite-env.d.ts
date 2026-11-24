/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_ENABLE_SPIKES?: string;
  readonly VITE_PRODUCT_NAME?: string;
  readonly VITE_AUTH_AUTHORITY?: string;
  readonly VITE_AUTH_CLIENT_ID?: string;
  readonly VITE_CONTROL_PLANE_URL?: string;
  readonly VITE_REGIONS?: string;
  readonly VITE_MESSAGING_ENABLED?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
