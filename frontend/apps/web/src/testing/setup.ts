import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
});

// The default 1 s wait fails on a starved CPU (parallel CI jobs, many test workers) although nothing is wrong; a
// real failure still fails, only later.
configure({ asyncUtilTimeout: 5000 });
