import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    projects: [
      'apps/*',
      'packages/*',
      {
        test: {
          name: 'tooling',
          include: ['eslint-rules/**/*.test.ts', 'tooling/**/*.test.ts'],
          environment: 'node',
          // The default 5 s is too short for tests that read the whole emoji image set on a busy machine.
          testTimeout: 15_000,
          hookTimeout: 15_000,
        },
      },
    ],
    passWithNoTests: true,
  },
});
