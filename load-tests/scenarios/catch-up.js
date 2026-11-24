// Reconnect storm: many clients reconnect at once and fetch what they missed with `after={lastSeenSeq}` (contract flow 4,
// step 5). Each iteration is one client: it learns the newest sequence number, pretends its last seen one is CATCHUP_LAG
// messages older, pages forward with `after` (oldest first), and optionally reconnects the live hub as well.
// The channels need at least CATCHUP_LAG messages: `provision` seeds SEED_MESSAGES (default 120) per channel.
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { THRESHOLDS, env, num } from '../lib/config.js';
import { orgs, pick, users } from '../lib/fixture.js';
import { tokenFor } from '../lib/tokens.js';
import { historyPage } from '../lib/api.js';
import { listen } from '../lib/signalr.js';

const STORM_RATE = num('STORM_RATE', 20); // reconnecting clients per second at the peak
const STORM_PEAK = env('STORM_PEAK', '30s');
const LAG = num('CATCHUP_LAG', 100);
const PAGE_SIZE = Math.min(num('PAGE_SIZE', 100), 100);
const WITH_HUB = env('CATCHUP_HUB', 'true') === 'true';

const pageMs = new Trend('catchup_page_ms', true);
const totalMs = new Trend('catchup_total_ms', true);
const wsReadyMs = new Trend('ws_ready_ms', true);
const catchUpFailed = new Counter('catchup_failed');
const wsErrors = new Counter('ws_errors');

export const options = {
  scenarios: {
    storm: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: Math.max(10, STORM_RATE),
      maxVUs: Math.max(50, STORM_RATE * 5),
      stages: [
        { target: STORM_RATE, duration: '10s' }, // the outage ends: clients come back quickly
        { target: STORM_RATE, duration: STORM_PEAK },
        { target: 1, duration: '10s' },
      ],
    },
  },
  thresholds: {
    catchup_page_ms: [`p(95)<${THRESHOLDS.nextPageP95}`],
    catchup_total_ms: [`p(95)<${THRESHOLDS.catchUpP95}`],
    ws_ready_ms: [`p(95)<${THRESHOLDS.wsReadyP95}`],
    catchup_failed: ['count==0'],
    'http_req_failed{kind:history}': ['rate<0.01'],
  },
};

export default function () {
  const user = pick(users);
  const org = orgs[user.orgIndex];
  const channelId = pick(org.channelIds);
  const token = tokenFor(user.index);
  const started = Date.now();

  const newest = historyPage(org, channelId, token, { limit: 1 });
  pageMs.add(newest.timings.duration);
  const latest = newest.status === 200 ? (newest.json('messages.0.seq') ?? 0) : null;
  if (latest === null) {
    catchUpFailed.add(1);
    return;
  }

  let after = Math.max(0, latest - LAG);
  let guard = 0;
  while (guard++ < 20) {
    const res = historyPage(org, channelId, token, { after, limit: PAGE_SIZE });
    pageMs.add(res.timings.duration);
    if (!check(res, { 'catch-up page ok': (r) => r.status === 200 })) {
      catchUpFailed.add(1);
      return;
    }
    const next = res.json('next');
    if (next === null || next === undefined) break;
    after = next;
  }
  totalMs.add(Date.now() - started);

  if (WITH_HUB) {
    listen({
      cellUrl: org.cellUrl,
      token,
      orgId: org.id,
      channelIds: [channelId],
      holdMs: 1000,
      onReady: (ms) => wsReadyMs.add(ms),
      onVisible: () => {},
      onError: () => wsErrors.add(1),
    });
  }
}
