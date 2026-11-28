/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly BACKROOM_TEST_CI?: string;
  readonly VITE_AUTH_AUTHORITY?: string;
  readonly VITE_AUTH_CLIENT_ID?: string;
  readonly VITE_CONTROL_PLANE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
