import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
});

// The default 1 s wait fails although nothing is wrong when the machine is busy; same values as the web app's setup.
// A real failure still fails, only later.
configure({ asyncUtilTimeout: import.meta.env.BACKROOM_TEST_CI ? 5000 : 3000 });
