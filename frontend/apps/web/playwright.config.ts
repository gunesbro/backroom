import { defineConfig, devices } from '@playwright/test';

// Browser smoke tests of the web app: real Chromium against the Vite development server, no backend. The pages under
// e2e/ render one piece of the app in isolation. Run with `pnpm -C frontend test:e2e`
// (browsers: `pnpm -C frontend/apps/web exec playwright install chromium`).
// Another port when 5179 is taken: BACKROOM_E2E_PORT.
const PORT = Number(process.env.BACKROOM_E2E_PORT ?? 5179);

export default defineConfig({
  testDir: './e2e',
  testMatch: '*.e2e.ts',
  forbidOnly: Boolean(process.env.CI),
  reporter: 'list',
  use: { baseURL: `http://localhost:${String(PORT)}` },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `pnpm exec vite --port ${String(PORT)} --strictPort`,
    url: `http://localhost:${String(PORT)}/e2e/composer.html`,
    reuseExistingServer: !process.env.CI,
    env: { VITE_RICH_TEXT_ENABLED: 'true' },
  },
});
