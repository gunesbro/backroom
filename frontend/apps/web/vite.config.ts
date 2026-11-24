import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  // Keeps Rust compiler output visible when the desktop app runs this dev server.
  clearScreen: false,
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
    // The desktop app (Tauri) loads this dev server; the port must stay fixed.
    port: 5173,
    strictPort: true,
    // The control plane (AppHost, http endpoint), so the browser calls one origin in development.
    proxy: {
      '/api': 'http://localhost:5100',
    },
  },
  test: {
    name: 'web',
    environment: 'jsdom',
    env: { BACKROOM_TEST_CI: process.env.CI ? '1' : '' },
    testTimeout: process.env.CI ? 15_000 : 5000,
    setupFiles: ['./src/testing/setup.ts'],
  },
});
