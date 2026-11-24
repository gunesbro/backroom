// Messaging load: senders post to public channels at a fixed arrival rate, readers page through history with keyset
// paging, and listeners hold a SignalR connection and measure how long a message takes to become visible.
// Run it after `node tools/loadtest.mjs provision` and with `node tools/loadtest.mjs serve` running (README).
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { DURATION, THRESHOLDS, env, num } from '../lib/config.js';
import { orgs, pick, userFor, users } from '../lib/fixture.js';
import { tokenFor } from '../lib/tokens.js';
import { historyPage, sendMessage } from '../lib/api.js';
import { listen } from '../lib/signalr.js';

const LISTENERS = num('LISTENERS', 10);
const SEND_RATE = num('SEND_RATE', 5); // messages per second, all senders together
const READ_RATE = num('READ_RATE', 5); // history reads per second
const MESSAGE_LENGTH = num('MESSAGE_LENGTH', 80);
const HISTORY_PAGES = num('HISTORY_PAGES', 3); // pages after the first one
const PAGE_SIZE = Math.min(num('PAGE_SIZE', 50), 100);
const durationSeconds = Number(/^(\d+)s$/.exec(DURATION)?.[1] ?? 60) + 5;

const sendMs = new Trend('send_ms', true);
const visibleMs = new Trend('message_visible_ms', true);
const firstPageMs = new Trend('history_first_page_ms', true);
const nextPageMs = new Trend('history_next_page_ms', true);
const wsReadyMs = new Trend('ws_ready_ms', true);
const sendFailed = new Counter('send_failed');
const wsErrors = new Counter('ws_errors');

export const options = {
  scenarios: {
    listeners: { executor: 'constant-vus', vus: LISTENERS, duration: DURATION, exec: 'listener' },
    senders: {
      executor: 'constant-arrival-rate',
      rate: SEND_RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: Math.max(5, Math.ceil(SEND_RATE / 2)),
      maxVUs: Math.max(20, SEND_RATE * 2),
      startTime: env('SEND_START', '5s'), // listeners subscribe first
      exec: 'sender',
    },
    readers: {
      executor: 'constant-arrival-rate',
      rate: READ_RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: Math.max(5, Math.ceil(READ_RATE / 2)),
      maxVUs: Math.max(20, READ_RATE * 2),
      startTime: env('SEND_START', '5s'),
      exec: 'reader',
    },
  },
  thresholds: {
    send_ms: [`p(95)<${THRESHOLDS.sendP95}`],
    message_visible_ms: [`p(95)<${THRESHOLDS.visibleP95}`],
    history_first_page_ms: [`p(95)<${THRESHOLDS.firstPageP95}`],
    history_next_page_ms: [`p(95)<${THRESHOLDS.nextPageP95}`],
    ws_ready_ms: [`p(95)<${THRESHOLDS.wsReadyP95}`],
    send_failed: ['count==0'],
    'http_req_failed{kind:send}': ['rate<0.01'],
    'http_req_failed{kind:history}': ['rate<0.01'],
  },
};

export function sender() {
  const user = pick(users);
  const org = orgs[user.orgIndex];
  const res = sendMessage(org, pick(org.channelIds), tokenFor(user.index), MESSAGE_LENGTH);
  sendMs.add(res.timings.duration);
  if (!check(res, { 'message created': (r) => r.status === 201 || r.status === 200 })) {
    sendFailed.add(1);
  }
}

export function reader() {
  const user = pick(users);
  const org = orgs[user.orgIndex];
  const channelId = pick(org.channelIds);
  const token = tokenFor(user.index);

  let res = historyPage(org, channelId, token, { limit: PAGE_SIZE });
  firstPageMs.add(res.timings.duration);
  check(res, { 'first page ok': (r) => r.status === 200 });
  // Keyset paging: each page's `next` is the `before` of the following (older) page. No offset anywhere.
  for (let page = 0; page < HISTORY_PAGES && res.status === 200; page++) {
    const next = res.json('next');
    if (next === null || next === undefined) break;
    res = historyPage(org, channelId, token, { before: next, limit: PAGE_SIZE });
    nextPageMs.add(res.timings.duration);
    check(res, { 'next page ok': (r) => r.status === 200 });
  }
}

export function listener() {
  const user = userFor(__VU);
  const org = orgs[user.orgIndex];
  listen({
    cellUrl: org.cellUrl,
    token: tokenFor(user.index),
    orgId: org.id,
    channelIds: org.channelIds,
    holdMs: durationSeconds * 1000,
    onReady: (ms) => wsReadyMs.add(ms),
    onVisible: (ms) => visibleMs.add(ms),
    onError: () => wsErrors.add(1),
  });
  // A failed connection must not spin: a timer keeps the iteration alive for at least a second. Not sleep(), which
  // blocks the event loop and would delay the socket's callbacks (and ws_ready_ms) by that second.
  setTimeout(() => {}, 1000);
}
