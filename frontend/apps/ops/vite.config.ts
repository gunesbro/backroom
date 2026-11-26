import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';
import { trustedTypes, trustedTypesCsp } from '../../tooling/trusted-types.ts';

export default defineConfig({
  // The operations console uses no HTML sink, so no Trusted Types policy is allowed (tooling/trusted-types.ts).
  plugins: [react(), trustedTypes(trustedTypesCsp([]))],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    // Read by tooling/check-bundle-size.js to tell initial from lazy chunks.
    manifest: true,
  },
  server: {
    port: 5174,
    strictPort: true,
  },
  test: {
    name: 'ops',
    environment: 'jsdom',
    setupFiles: ['./src/testing/setup.ts'],
  },
});
