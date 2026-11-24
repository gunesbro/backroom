import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
});

// On a shared CI machine the default 1 s wait can fail although nothing is wrong; a real failure still fails.
if (import.meta.env.BACKROOM_TEST_CI) configure({ asyncUtilTimeout: 5000 });
