import http from 'k6/http';
import { TOKEN_SERVER } from './config.js';

// Access tokens live 5 minutes. The token server (tools/loadtest.mjs serve) keeps every user's refresh chain
// alive and answers with the current cell token, so no token is stored in a file and a run is not limited to 5 minutes.
const cache = new Map(); // per VU: user index -> { token, expiresAt }
const SAFETY_MS = 45_000;

export function tokenFor(index) {
  const cached = cache.get(index);
  if (cached && cached.expiresAt - Date.now() > SAFETY_MS) {
    return cached.token;
  }
  const res = http.get(`${TOKEN_SERVER}/token/${index}`, { tags: { kind: 'token' } });
  if (res.status !== 200) {
    throw new Error(`token server answered ${res.status} for user ${index}`);
  }
  const body = res.json();
  cache.set(index, { token: body.accessToken, expiresAt: body.expiresAt });
  return body.accessToken;
}

export function authHeaders(token) {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
}
