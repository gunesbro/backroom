#!/usr/bin/env node
// Checks that no acknowledged message is lost when PostgreSQL crashes under load (docs/adr/0012).
// It sends messages to one channel at a fixed rate and remembers every acknowledged one (201 or 200) with its sequence
// number. While it runs, crash the database of the load-test stack from another terminal, for example by killing one
// of its server processes (PostgreSQL then restarts itself through crash recovery and loses whatever was not flushed):
//
//   docker exec <postgres container> sh -c 'kill -9 $(pgrep -f "walwriter")'
//
// Afterwards it reads the channel back through the API and reports acknowledged messages that are missing, and
// sequence numbers that were acknowledged for one message but now belong to another. Both must be 0.
// Needs the fixture and the token server of loadtest.mjs (README). Never run it against a stack you care about.
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const env = (name, fallback) => process.env[name] || fallback;
const fixture = JSON.parse(await readFile(resolve(env('FIXTURE', resolve(here, '../.data/fixture.json'))), 'utf8'));
const tokenServer = env('TOKEN_SERVER', 'http://127.0.0.1:5999');
const rate = Number(env('RATE', '100')); // sends per second
const seconds = Number(env('SECONDS', '12'));
const settleSeconds = Number(env('SETTLE_SECONDS', '8')); // lets the stack recover before the channel is read back

const org = fixture.orgs[0];
if (!['localhost', '127.0.0.1', '[::1]'].includes(new URL(org.cellUrl).hostname) && process.env.LOADTEST_ALLOW_REMOTE !== 'yes') {
  throw new Error('Refusing a non-local target.');
}
const channelId = org.channelIds[0];
const users = fixture.users.filter((user) => user.orgIndex === 0);
const messages = `${org.cellUrl.replace(/\/$/, '')}/api/v1/orgs/${org.id}/channels/${channelId}/messages`;
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

const tokens = new Map();
for (const user of users) {
  const res = await fetch(`${tokenServer}/token/${user.index}`);
  if (!res.ok) throw new Error(`token server answered ${res.status}`);
  tokens.set(user.index, (await res.json()).accessToken);
}
const headers = (user) => ({ Authorization: `Bearer ${tokens.get(user.index)}`, 'Content-Type': 'application/json' });

const acknowledged = new Map(); // clientMessageId -> seq
let failed = 0;
const outcomes = {}; // status code or "no answer" -> count
const count = (outcome) => { outcomes[outcome] = (outcomes[outcome] ?? 0) + 1; };
const inFlight = new Set();
async function send() {
  const user = users[Math.floor(Math.random() * users.length)];
  const clientMessageId = randomUUID();
  try {
    const res = await fetch(messages, {
      method: 'POST',
      headers: headers(user),
      body: JSON.stringify({ clientMessageId, text: `crash-check ${clientMessageId}` }),
      signal: AbortSignal.timeout(10_000),
    });
    count(res.status);
    if (res.status === 201 || res.status === 200) {
      acknowledged.set(clientMessageId, (await res.json()).seq);
    } else {
      failed++;
    }
  } catch {
    count('no answer');
    failed++; // expected while the database restarts
  }
}

const startedAt = Date.now();
console.log(`[crash-check] sending ${rate}/s for ${seconds}s; crash the database now`);
for (let n = 0; Date.now() - startedAt < seconds * 1000; n++) {
  const sending = send();
  inFlight.add(sending);
  sending.finally(() => inFlight.delete(sending));
  const due = startedAt + ((n + 1) * 1000) / rate;
  if (due > Date.now()) await sleep(due - Date.now());
}
await Promise.all(inFlight);
await sleep(settleSeconds * 1000);

// Read the whole channel back, oldest first, with the catch-up cursor.
const stored = new Map(); // clientMessageId -> seq
for (let after = 0, attempts = 0; ;) {
  let page;
  try {
    const res = await fetch(`${messages}?after=${after}&limit=100`, { headers: headers(users[0]), signal: AbortSignal.timeout(10_000) });
    if (!res.ok) throw new Error(`status ${res.status}`);
    page = await res.json();
  } catch (error) {
    if (++attempts > 30) throw error;
    await sleep(1000);
    continue;
  }
  for (const message of page.messages) stored.set(message.clientMessageId, message.seq);
  if (page.next === null || page.next === undefined) break;
  after = page.next;
}

let lost = 0;
let renumbered = 0;
for (const [clientMessageId, seq] of acknowledged) {
  if (!stored.has(clientMessageId)) lost++;
  else if (stored.get(clientMessageId) !== seq) renumbered++;
}
const seqOwners = new Map([...stored].map(([clientMessageId, seq]) => [seq, clientMessageId]));
let reused = 0;
for (const [clientMessageId, seq] of acknowledged) {
  if (!stored.has(clientMessageId) && seqOwners.has(seq)) reused++;
}
console.log(JSON.stringify({ acknowledged: acknowledged.size, failedSends: failed, outcomes, stored: stored.size, acknowledgedButLost: lost, acknowledgedSeqNowAnotherMessage: reused, renumbered }));
process.exit(lost + reused + renumbered === 0 ? 0 : 1);
