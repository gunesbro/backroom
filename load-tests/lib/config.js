// Configuration comes from environment variables (k6: -e NAME=value or the shell environment).
export function env(name, fallback) {
  const value = __ENV[name];
  return value === undefined || value === '' ? fallback : value;
}

export function num(name, fallback) {
  const value = Number(env(name, fallback));
  if (!Number.isFinite(value)) {
    throw new Error(`${name} must be a number`);
  }
  return value;
}

export const DURATION = env('DURATION', '60s');
export const TOKEN_SERVER = env('TOKEN_SERVER', 'http://127.0.0.1:5999');

// Thresholds (milliseconds). Defaults follow the budget in CLAUDE.md ("Performance and resources"): a message is
// visible on another client at p95 300 ms; the others are engineering targets for a laptop-sized run.
export const THRESHOLDS = {
  sendP95: num('THRESHOLD_SEND_P95_MS', 250),
  visibleP95: num('THRESHOLD_VISIBLE_P95_MS', 300),
  firstPageP95: num('THRESHOLD_FIRST_PAGE_P95_MS', 150),
  nextPageP95: num('THRESHOLD_NEXT_PAGE_P95_MS', 150),
  catchUpP95: num('THRESHOLD_CATCHUP_P95_MS', 500),
  wsReadyP95: num('THRESHOLD_WS_READY_P95_MS', 1000),
};
