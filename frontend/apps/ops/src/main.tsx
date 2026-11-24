import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from '@/app/app';
import { createOidcAuthClient } from '@/auth/oidc-auth-client';
import { config } from '@/config';

// After signing out (or in), going back may restore this page from the back-forward cache with the previous
// account's screen; reload it so it starts from the current session.
window.addEventListener('pageshow', (event) => {
  if (event.persisted) window.location.reload();
});

const root = document.getElementById('root');
if (!root) {
  throw new Error('Root element #root is missing from index.html.');
}

const auth = createOidcAuthClient({ authority: config.auth.authority(), clientId: config.auth.clientId });

createRoot(root).render(
  <StrictMode>
    <App auth={auth} />
  </StrictMode>,
);
