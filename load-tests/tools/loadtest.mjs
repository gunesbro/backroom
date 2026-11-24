#!/usr/bin/env node
// Setup tool for the k6 scenarios (see load-tests/README.md). Node 20 or newer, no dependencies.
//
//   provision  create test users in Keycloak, organizations, channels, seed messages and memberships; write the fixture
//   serve      sign every fixture user in, keep their tokens fresh and hand cell tokens to k6 over loopback HTTP
//   cleanup    delete the fixture's users from Keycloak
//
// Everything is configured with environment variables (README). Passwords and tokens are never printed or committed:
// the fixture (git-ignored, .data/) holds the throwaway passwords of a throwaway stack, and tokens stay in memory.
import { randomBytes } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Keycloak, KeycloakAdmin } from './keycloak.mjs';
import { assertSafeFixture, assertSafeTargets } from './targets.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const env = (name, fallback) => process.env[name] || fallback;
const num = (name, fallback) => Number(env(name, String(fallback)));

const config = {
  // No defaults for the endpoints: a forgotten variable must never turn into the development stack (targets.mjs).
  keycloakUrl: process.env.KEYCLOAK_URL,
  realm: env('KEYCLOAK_REALM', 'backroom'),
  controlPlane: process.env.CONTROL_PLANE_URL?.replace(/\/$/, ''),
  mailpit: process.env.MAILPIT_URL?.replace(/\/$/, ''),
  region: env('REGION', 'eu-dev'),
  orgs: num('ORGS', 2),
  members: num('MEMBERS', 5), // per organization, the owner included
  channels: num('CHANNELS', 2), // per organization
  seedMessages: num('SEED_MESSAGES', 120), // per channel
  concurrency: num('CONCURRENCY', 4),
  fixture: resolve(env('FIXTURE', resolve(here, '../.data/fixture.json'))),
  port: num('TOKEN_SERVER_PORT', 5999),
  refreshSeconds: num('REFRESH_SECONDS', 150), // access tokens live 300 s
};

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));
const log = (message) => console.log(`[loadtest] ${message}`);

// Load tests create users and organizations and send many messages: only ever against a throwaway stack that was
// named explicitly, never the development stack (targets.mjs).
function refuseUnsafeEndpoints() {
  assertSafeTargets({ KEYCLOAK_URL: config.keycloakUrl, CONTROL_PLANE_URL: config.controlPlane, MAILPIT_URL: config.mailpit });
}

async function pool(items, size, work) {
  const queue = items.map((item, index) => [item, index]);
  const workers = Array.from({ length: Math.min(size, queue.length) }, async () => {
    while (queue.length > 0) {
      const [item, index] = queue.shift();
      await work(item, index);
    }
  });
  await Promise.all(workers);
}

async function api(method, url, token, body) {
  const response = await fetch(url, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`${method} ${new URL(url).pathname} answered ${response.status}`);
  }
  return text ? JSON.parse(text) : null;
}

async function sharedCellScopes(controlPlane) {
  const { cells } = await (await fetch(`${controlPlane}/api/v1/cells/shared`)).json();
  return cells.map((cell) => `cell:${cell.id}`);
}

async function inviteCode(email) {
  // Outside production all email goes to Mailpit; the link is https://<web>/invite#<code>.
  for (let attempt = 0; attempt < 60; attempt++) {
    const search = await (await fetch(`${config.mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json();
    const id = search.messages?.[0]?.ID;
    if (id) {
      const message = await (await fetch(`${config.mailpit}/api/v1/message/${id}`)).json();
      const code = /\/invite#([A-Za-z0-9_-]{20,})/.exec(message.Text ?? '')?.[1];
      if (code) return code;
    }
    await sleep(1000);
  }
  throw new Error('no invitation email arrived in Mailpit within 60 s');
}

async function provision() {
  refuseUnsafeEndpoints();
  const adminUser = process.env.KEYCLOAK_ADMIN_USER;
  const adminPassword = process.env.KEYCLOAK_ADMIN_PASSWORD;
  if (!adminUser || !adminPassword) {
    throw new Error('Set KEYCLOAK_ADMIN_USER and KEYCLOAK_ADMIN_PASSWORD (the Keycloak administrator of the load-test stack).');
  }
  if (config.members < 1 || config.members > 49) {
    // The owner may send 50 invitations per day (contract flow 3).
    throw new Error('MEMBERS must be 1 to 49 (an owner sends at most 50 invitations a day).');
  }

  const keycloak = new Keycloak(config.keycloakUrl, config.realm);
  const admin = new KeycloakAdmin(config.keycloakUrl, config.realm, adminUser, adminPassword);
  const scopes = ['control-plane', ...(await sharedCellScopes(config.controlPlane))];
  const run = randomBytes(3).toString('hex');
  const password = randomBytes(18).toString('base64url');

  const users = [];
  const orgs = [];
  for (let o = 0; o < config.orgs; o++) {
    orgs.push({ index: o, name: `Load ${run} ${o}`, id: null, cellId: null, cellUrl: null, channelIds: [] });
    for (let m = 0; m < config.members; m++) {
      users.push({ index: users.length, orgIndex: o, owner: m === 0, email: `lt-${run}-${o}-${m}@loadtest.test`, password });
    }
  }

  log(`creating ${users.length} users in realm ${config.realm}`);
  await pool(users, config.concurrency, async (user) => {
    const status = await admin.createUser(user.email, user.password, `Load ${user.orgIndex}-${user.index}`);
    if (status !== 201) throw new Error(`creating a user answered ${status}`);
  });
  // Written first, so cleanup can remove the users even if a later step fails.
  await save({ run, keycloak: config.keycloakUrl, realm: config.realm, controlPlane: config.controlPlane, users, orgs });

  await pool(orgs, config.concurrency, async (org) => {
    const members = users.filter((u) => u.orgIndex === org.index);
    const owner = await keycloak.signIn(members[0].email, members[0].password, scopes);

    const cpToken = (await owner.token('control-plane')).accessToken;
    const created = await api('POST', `${config.controlPlane}/api/v1/organizations`, cpToken, { name: org.name, region: config.region });
    for (let i = 0; ; i++) {
      const state = await api('GET', `${config.controlPlane}/api/v1/organizations/${created.id}`, cpToken);
      if (state.status === 'active') break;
      if (state.status === 'failed' || i > 60) throw new Error(`organization ${org.index} is ${state.status} (${state.code ?? 'timeout'})`);
      await sleep(1000);
    }
    Object.assign(org, { id: created.id, cellId: created.cellId, cellUrl: created.cellUrl.replace(/\/$/, '') });

    const cell = (await owner.token(`cell:${org.cellId}`)).accessToken;
    const base = `${org.cellUrl}/api/v1/orgs/${org.id}`;
    for (let c = 0; c < config.channels; c++) {
      org.channelIds.push((await api('POST', `${base}/channels`, cell, { name: `load-${c}` })).id);
    }
    for (const channelId of org.channelIds) {
      for (let n = 0; n < config.seedMessages; n++) {
        await api('POST', `${base}/conversations/${channelId}/messages`, cell, { clientMessageId: crypto.randomUUID(), text: `seed ${n}` });
      }
    }

    // Membership only exists through invitations (contract flow 3). Setup uses it; load runs never do.
    for (const member of members.slice(1)) {
      await api('POST', `${base}/invitations`, cell, { email: member.email });
    }
    await pool(members.slice(1), 3, async (member) => {
      const code = await inviteCode(member.email);
      const session = await keycloak.signIn(member.email, member.password, scopes);
      const token = (await session.token('control-plane')).accessToken;
      await api('POST', `${config.controlPlane}/api/v1/invitations/accept`, token, { code });
      for (let i = 0; ; i++) {
        const state = await api('GET', `${config.controlPlane}/api/v1/invitations/acceptance`, token);
        if (state.status === 'used') break;
        if (state.status === 'failed' || i > 60) throw new Error(`acceptance is ${state.status} (${state.code ?? 'timeout'})`);
        await sleep(1000);
      }
    });
    log(`organization ${org.index} ready on cell ${org.cellId} with ${members.length} members`);
  });

  await save({ run, keycloak: config.keycloakUrl, realm: config.realm, controlPlane: config.controlPlane, users, orgs });
  log(`fixture written to ${config.fixture}`);
}

async function save(fixture) {
  await mkdir(dirname(config.fixture), { recursive: true });
  await writeFile(config.fixture, JSON.stringify(fixture, null, 1), { mode: 0o600 });
}

async function load() {
  return JSON.parse(await readFile(config.fixture, 'utf8'));
}

async function serve() {
  const fixture = await load();
  assertSafeFixture(fixture);
  const keycloak = new Keycloak(fixture.keycloak, fixture.realm);
  const scopes = ['control-plane', ...(await sharedCellScopes(fixture.controlPlane))];
  const state = new Map(); // user index -> { session, scope, token: { accessToken, expiresAt } }

  async function signInUser(user) {
    const org = fixture.orgs[user.orgIndex];
    const scope = `cell:${org.cellId}`;
    const session = await keycloak.signIn(user.email, user.password, scopes);
    state.set(user.index, { session, scope, token: await session.token(scope) });
  }

  log(`signing in ${fixture.users.length} users`);
  await pool(fixture.users, config.concurrency, signInUser);

  // Each user's chain is refreshed by this one loop only, so a refresh token is never used twice at once.
  async function refreshLoop() {
    for (;;) {
      await sleep(config.refreshSeconds * 1000);
      let failed = 0;
      await pool(fixture.users, config.concurrency, async (user) => {
        const entry = state.get(user.index);
        try {
          entry.token = await entry.session.token(entry.scope);
        } catch {
          try {
            await signInUser(user); // the refresh chain broke; sign in again
          } catch {
            failed++;
          }
        }
      });
      log(`refreshed tokens, ${failed} failed`);
    }
  }
  void refreshLoop();

  createServer((request, response) => {
    const index = /^\/token\/(\d+)$/.exec(request.url ?? '')?.[1];
    const entry = index === undefined ? undefined : state.get(Number(index));
    if (request.method !== 'GET' || !entry) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    response.end(JSON.stringify(entry.token));
  }).listen(config.port, '127.0.0.1', () => log(`token server on http://127.0.0.1:${config.port}; start k6 now (Ctrl+C stops it)`));
}

async function cleanup() {
  const fixture = await load();
  assertSafeFixture(fixture);
  const admin = new KeycloakAdmin(fixture.keycloak, fixture.realm, process.env.KEYCLOAK_ADMIN_USER, process.env.KEYCLOAK_ADMIN_PASSWORD);
  let deleted = 0;
  await pool(fixture.users, config.concurrency, async (user) => {
    // Not "deleted += await ...": that reads the total before the await, so concurrent workers lose each other's counts.
    const count = await admin.deleteByEmail(user.email);
    deleted += count;
  });
  log(`deleted ${deleted} Keycloak users; the organizations stay in the cells (reset the stack's volumes to drop them)`);
}

const commands = { provision, serve, cleanup };
const command = commands[process.argv[2]];
if (!command) {
  console.error('usage: node tools/loadtest.mjs provision | serve | cleanup');
  process.exit(2);
}
command().catch((error) => {
  console.error(`[loadtest] ${error.message}`);
  process.exit(1);
});
