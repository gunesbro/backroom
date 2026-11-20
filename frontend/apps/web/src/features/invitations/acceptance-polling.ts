const FIRST_POLL_MS = 1000;
const LONGEST_POLL_MS = 10_000;
/** 1, 2, 4 and 8 seconds, then every 10 seconds: about two minutes, when the operations panel raises an alert. */
const MAX_POLLS = 16;

/** How long to wait after the given number of answers before asking again; false once waiting stops. */
export function acceptancePollDelay(polls: number): number | false {
  return polls >= MAX_POLLS ? false : Math.min(FIRST_POLL_MS * 2 ** Math.max(0, polls - 1), LONGEST_POLL_MS);
}
