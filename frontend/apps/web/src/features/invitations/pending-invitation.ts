import { readItem, writeItem } from '@/shared/storage';
import { isInvitationCode } from './invitation-code';

// The invitation code is a secret (docs/contracts/phase-1.md, "Invitation security" → "Transport"). It lives only
// in local storage as `{ code, expiresAt }`, so a verification email link that finishes sign-in in another tab still
// finds it. It is removed after accepting, when it is found invalid, on sign-out and once it has expired: never in
// a URL, the OIDC state, a query key or a log.

/** The pending invitation code and its expiry, shared by the browser's tabs. */
export const INVITATION_CODE_KEY = 'ph.invitations.code';
/** How long a kept code waits for sign-in to finish. */
export const INVITATION_CODE_EXPIRES_MS = 30 * 60 * 1000;
/** When this tab accepted an invitation (no secret), so a reload keeps waiting for it. */
export const INVITATION_ACCEPTING_KEY = 'ph.invitations.accepting';
/** After this a reload stops waiting: the acceptance has long settled or shows in the operations panel. */
export const ACCEPTING_EXPIRES_MS = 6 * 60 * 60 * 1000;

export const INVITE_PATH = '/invite';

/**
 * Set when this page load opened a malformed link: the page shows the invalid link even though another tab keeps a
 * valid code. In memory only (never storage or the URL), so it ends with the page load.
 */
let malformedLink = false;

/** The clock, injectable so tests control time. */
type Clock = () => number;
const systemClock: Clock = () => Date.now();

function storeCode(code: string | undefined, now: Clock): void {
  writeItem(
    'local',
    INVITATION_CODE_KEY,
    code === undefined ? undefined : JSON.stringify({ code, expiresAt: now() + INVITATION_CODE_EXPIRES_MS }),
  );
}

/**
 * Takes the code out of an invitation link (`/invite#<code>`): removes the fragment from the address bar and the
 * history entry, and keeps the code in local storage across the sign-in redirect. Runs before anything else on
 * the page (main.tsx), so no router, monitoring or session recording ever sees the fragment.
 */
export function captureInvitationCode(now: Clock = systemClock): void {
  const { pathname, search, hash } = window.location;
  if (pathname !== INVITE_PATH || hash === '') {
    return;
  }

  window.history.replaceState(window.history.state, '', `${pathname}${search}`);

  // A new link replaces whatever this tab was doing with an older one. A malformed fragment is never stored and
  // leaves a code another tab is waiting on alone; without one, the page shows the invalid link.
  const code = hash.slice(1);
  if (!isInvitationCode(code)) {
    malformedLink = true;
    return;
  }

  malformedLink = false;
  storeCode(code, now);
  writeItem('session', INVITATION_ACCEPTING_KEY, undefined);
}

/** The code the invite page works with: none when this page load opened a malformed link. */
export function readInvitationCodeForPage(now: Clock = systemClock): string | undefined {
  return malformedLink ? undefined : readInvitationCode(now);
}

/** One-time sweep at app start: removes an expired or malformed kept code, so a bearer code never lingers on disk. */
export function sweepInvitationCode(now: Clock = systemClock): void {
  readInvitationCode(now);
}

/** A pasted link or code (the onboarding join card), kept like one from an opened link. */
export function saveInvitationCode(code: string, now: Clock = systemClock): void {
  if (!isInvitationCode(code)) {
    throw new Error('Not an invitation code.');
  }

  malformedLink = false;
  storeCode(code, now);
  writeItem('session', INVITATION_ACCEPTING_KEY, undefined);
}

/** The kept code, or nothing: an expired or malformed entry is deleted and counts as absent. */
export function readInvitationCode(now: Clock = systemClock): string | undefined {
  const raw = readItem('local', INVITATION_CODE_KEY);
  if (raw === undefined) {
    return undefined;
  }

  let entry: unknown;
  try {
    entry = JSON.parse(raw);
  } catch {
    entry = undefined;
  }

  if (
    typeof entry === 'object' &&
    entry !== null &&
    'code' in entry &&
    'expiresAt' in entry &&
    typeof entry.code === 'string' &&
    typeof entry.expiresAt === 'number' &&
    isInvitationCode(entry.code) &&
    now() < entry.expiresAt &&
    entry.expiresAt <= now() + INVITATION_CODE_EXPIRES_MS
  ) {
    return entry.code;
  }

  writeItem('local', INVITATION_CODE_KEY, undefined);
  return undefined;
}

/**
 * Removes the kept code. With `only`, just that code: a tab that settles its own invitation must not delete a newer
 * link another tab has stored since. Without it (sign-out), whatever is kept.
 */
export function clearInvitationCode(only?: string): void {
  if (only !== undefined && readInvitationCode() !== only) {
    return;
  }

  writeItem('local', INVITATION_CODE_KEY, undefined);
}

export function isAccepting(): boolean {
  const since = Number(readItem('session', INVITATION_ACCEPTING_KEY));
  const age = Date.now() - since;
  if (since > 0 && age >= 0 && age < ACCEPTING_EXPIRES_MS) {
    return true;
  }

  writeItem('session', INVITATION_ACCEPTING_KEY, undefined);
  return false;
}

export function setAccepting(accepting: boolean): void {
  writeItem('session', INVITATION_ACCEPTING_KEY, accepting ? String(Date.now()) : undefined);
}

/** Whether this tab shows the invite page: an invitation link, a kept code, or an acceptance being waited for. */
export function hasPendingInvitation(now: Clock = systemClock): boolean {
  return window.location.pathname === INVITE_PATH || readInvitationCode(now) !== undefined || isAccepting();
}

/** Leaves the invite page's address once the invitation is settled, so a reload opens the app. */
export function leaveInvitePath(): void {
  malformedLink = false;
  if (window.location.pathname === INVITE_PATH) {
    window.history.replaceState(window.history.state, '', '/');
  }
}
