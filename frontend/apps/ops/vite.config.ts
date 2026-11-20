import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
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
