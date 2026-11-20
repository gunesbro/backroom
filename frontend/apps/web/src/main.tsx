// Must stay the first import: it removes an invitation code from the address bar before anything else runs.
import './capture-invitation';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
// Self-hosted, so no font request leaves for a third party. Each file loads only the scripts a page uses.
import '@fontsource/ibm-plex-sans/400.css';
import '@fontsource/ibm-plex-sans/600.css';
import '@fontsource/ibm-plex-sans/700.css';
import { App } from '@/app/app';

// After signing out (or in), going back may restore this page from the back-forward cache with the previous
// account's screen; reload it so it starts from the current session.
window.addEventListener('pageshow', (event) => {
  if (event.persisted) window.location.reload();
});

const root = document.getElementById('root');
if (!root) {
  throw new Error('Root element #root is missing from index.html.');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
