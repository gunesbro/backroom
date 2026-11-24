// Keycloak sign-in for the load-test tools: the authorization code flow with PKCE, driven through the login page like a
// browser does (no direct grant exists in the realm and none is enabled). It signs in with the public client `desktop`,
// whose redirect URI is the loopback address; the redirect is read from the Location header and never followed.
import { createHash, randomBytes } from 'node:crypto';

const REDIRECT_URI = 'http://127.0.0.1/callback';
const CLIENT_ID = 'desktop';

const b64url = (buffer) => buffer.toString('base64url');

function decodeHtml(text) {
  return text.replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#x3D;/gi, '=').replace(/&#39;/g, "'");
}

class CookieJar {
  #cookies = new Map();
  store(response) {
    for (const line of response.headers.getSetCookie()) {
      const [pair] = line.split(';');
      const eq = pair.indexOf('=');
      this.#cookies.set(pair.slice(0, eq).trim(), pair.slice(eq + 1));
    }
  }
  header() {
    return [...this.#cookies].map(([k, v]) => `${k}=${v}`).join('; ');
  }
}

async function step(jar, url, init = {}) {
  const response = await fetch(url, { ...init, redirect: 'manual', headers: { ...init.headers, Cookie: jar.header() } });
  jar.store(response);
  return response;
}

export class Keycloak {
  constructor(baseUrl, realm) {
    this.base = baseUrl.replace(/\/$/, '');
    this.realm = realm;
    this.tokenUrl = `${this.base}/realms/${realm}/protocol/openid-connect/token`;
  }

  /** Signs in and returns a Session whose refresh token chain can be narrowed to one scope at a time. */
  async signIn(email, password, scopes) {
    const verifier = b64url(randomBytes(32));
    const state = b64url(randomBytes(16));
    const params = new URLSearchParams({
      client_id: CLIENT_ID,
      response_type: 'code',
      redirect_uri: REDIRECT_URI,
      scope: ['openid', ...scopes].join(' '),
      state,
      nonce: b64url(randomBytes(16)),
      code_challenge: b64url(createHash('sha256').update(verifier).digest()),
      code_challenge_method: 'S256',
    });
    const jar = new CookieJar();
    const page = await step(jar, `${this.base}/realms/${this.realm}/protocol/openid-connect/auth?${params}`);
    const html = await page.text();
    const action = /<form[^>]*\bid="kc-form-login"[^>]*\baction="([^"]+)"/.exec(html)?.[1]
      ?? /<form[^>]*\baction="([^"]*login-actions\/authenticate[^"]*)"/.exec(html)?.[1];
    if (page.status !== 200 || !action) {
      throw new Error(`Keycloak sign-in page not found (status ${page.status})`);
    }

    let response = await step(jar, decodeHtml(action), {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ username: email, password, credentialId: '' }),
    });
    for (let hops = 0; hops < 5 && response.status >= 300 && response.status < 400; hops++) {
      const location = response.headers.get('location');
      if (location.startsWith(REDIRECT_URI)) {
        const result = new URL(location).searchParams;
        if (result.get('state') !== state || !result.get('code')) {
          throw new Error(`sign-in did not return a code (${result.get('error') ?? 'state mismatch'})`);
        }
        const tokens = await this.#token({
          grant_type: 'authorization_code',
          code: result.get('code'),
          code_verifier: verifier,
          redirect_uri: REDIRECT_URI,
        });
        return new Session(this, tokens.refresh_token);
      }
      response = await step(jar, new URL(location, this.base).href);
    }
    throw new Error(`sign-in did not finish (status ${response.status}); a required action or a wrong password?`);
  }

  async #token(form) {
    const response = await fetch(this.tokenUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ client_id: CLIENT_ID, ...form }),
    });
    const body = await response.json();
    if (!response.ok) {
      throw new Error(`token request failed: ${body.error ?? response.status}`);
    }
    return body;
  }

  refresh(refreshToken, scope) {
    return this.#token({ grant_type: 'refresh_token', refresh_token: refreshToken, scope });
  }
}

/**
 * One refresh token chain. Refresh tokens are single use, so requests run one after another (contract: "One refresh at a
 * time"). The first answer after sign-in carries every audience and is discarded; each later one names one target.
 */
export class Session {
  #keycloak;
  #refreshToken;
  #queue = Promise.resolve();

  constructor(keycloak, refreshToken) {
    this.#keycloak = keycloak;
    this.#refreshToken = refreshToken;
  }

  /** @returns {Promise<{accessToken: string, expiresAt: number}>} */
  token(scope) {
    const run = this.#queue.then(async () => {
      const body = await this.#keycloak.refresh(this.#refreshToken, scope);
      this.#refreshToken = body.refresh_token;
      if (!body.scope.split(' ').includes(scope)) {
        throw new Error(`Keycloak did not grant scope ${scope}`);
      }
      return { accessToken: body.access_token, expiresAt: Date.now() + body.expires_in * 1000 };
    });
    this.#queue = run.catch(() => {});
    return run;
  }
}

export class KeycloakAdmin {
  constructor(baseUrl, realm, user, password) {
    this.base = baseUrl.replace(/\/$/, '');
    this.realm = realm;
    this.user = user;
    this.password = password;
  }

  #cached = { token: null, until: 0 };

  async #bearer() {
    if (this.#cached.token && Date.now() < this.#cached.until) return this.#cached.token;
    // Keycloak's built-in admin client in the master realm; this is not a client of the Backroom realm.
    const response = await fetch(`${this.base}/realms/master/protocol/openid-connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: this.user, password: this.password }),
    });
    if (!response.ok) throw new Error(`admin sign-in failed (${response.status})`);
    const token = (await response.json()).access_token;
    this.#cached = { token, until: Date.now() + 30_000 };
    return token;
  }

  async #call(method, path, body) {
    const response = await fetch(`${this.base}/admin/realms/${this.realm}${path}`, {
      method,
      headers: { Authorization: `Bearer ${await this.#bearer()}`, 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok && response.status !== 404) {
      throw new Error(`${method} ${path} answered ${response.status}`);
    }
    return response;
  }

  /** A verified, enabled test user with a permanent password. */
  async createUser(email, password, displayName) {
    const response = await this.#call('POST', '/users', {
      username: email,
      email,
      emailVerified: true,
      enabled: true,
      firstName: displayName,
      lastName: 'Load',
      credentials: [{ type: 'password', value: password, temporary: false }],
    });
    return response.status;
  }

  async deleteByEmail(email) {
    const found = await (await this.#call('GET', `/users?email=${encodeURIComponent(email)}&exact=true`)).json();
    for (const user of found) {
      await this.#call('DELETE', `/users/${user.id}`);
    }
    return found.length;
  }
}
