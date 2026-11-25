import type { CustomStatus } from '@backroom/realtime';
import type { AccountStatus, StatusInput } from './api';

/** The server's limits (contract, `PUT /api/v1/me/status`); checked here first so the person sees why at once. */
export const MAX_STATUS_TEXT = 100;
export const MAX_DND_MS = 7 * 24 * 60 * 60_000;
export const MAX_STATUS_EXPIRY_MS = 30 * 24 * 60 * 60_000;

const segmenter = new Intl.Segmenter('en-US', { granularity: 'grapheme' });

/** Characters as people count them (an emoji with skin tone or a flag is one). */
export function graphemeCount(value: string): number {
  return Array.from(segmenter.segment(value)).length;
}

// A pictograph, a flag letter or a whole keycap such as 1️⃣ (a digit, # or * with the keycap mark, as the server
// takes them; the mark on its own or on a letter is not an emoji).
const PICTOGRAPHIC = /\p{Extended_Pictographic}|\p{Regional_Indicator}|^[0-9#*]️?⃣$/u;

/** Exactly one emoji (one grapheme with a pictograph in it). */
export function isSingleEmoji(value: string): boolean {
  return graphemeCount(value) === 1 && PICTOGRAPHIC.test(value);
}

// Control characters, line and paragraph separators, and bidirectional controls (they can disguise text).
// Written as escapes, so the source itself carries no bidirectional characters.
const FORBIDDEN_TEXT = /[\p{Cc}\p{Zl}\p{Zp}]|[\u200E\u200F\u202A-\u202E\u2066-\u2069]/u;

export type CustomStatusProblem = 'statusEmpty' | 'emojiInvalid' | 'textInvalid' | 'expiryInvalid';

/** The custom status as the server will take it, or why not. Text is trimmed; an empty part is null. */
export function readCustomStatus(
  emoji: string,
  text: string,
  expiresAt: Date | null,
  now: Date,
): { status: CustomStatus } | { problem: CustomStatusProblem } {
  const trimmedEmoji = emoji.trim();
  const trimmedText = text.trim();
  if (trimmedEmoji === '' && trimmedText === '') return { problem: 'statusEmpty' };
  if (trimmedEmoji !== '' && !isSingleEmoji(trimmedEmoji)) return { problem: 'emojiInvalid' };
  if (graphemeCount(trimmedText) > MAX_STATUS_TEXT || FORBIDDEN_TEXT.test(trimmedText)) {
    return { problem: 'textInvalid' };
  }
  if (expiresAt) {
    const ms = expiresAt.getTime() - now.getTime();
    if (Number.isNaN(ms) || ms <= 0 || ms > MAX_STATUS_EXPIRY_MS) return { problem: 'expiryInvalid' };
  }
  return {
    status: {
      emoji: trimmedEmoji === '' ? null : trimmedEmoji,
      text: trimmedText === '' ? null : trimmedText,
      expiresAt: expiresAt ? expiresAt.toISOString() : null,
    },
  };
}

/** Whether a do-not-disturb end time is a valid choice now. */
export function isValidDndUntil(until: Date, now: Date): boolean {
  const ms = until.getTime() - now.getTime();
  return !Number.isNaN(ms) && ms > 0 && ms <= MAX_DND_MS;
}

/** "Until tomorrow": 09:00 on the next day in the person's local time (as the contract's mute "until tomorrow"). */
export function tomorrowMorning(now: Date): Date {
  const next = new Date(now);
  next.setDate(next.getDate() + 1);
  next.setHours(9, 0, 0, 0);
  return next;
}

/** The end of today in local time (custom status "Today"). */
export function endOfToday(now: Date): Date {
  const end = new Date(now);
  end.setHours(23, 59, 0, 0);
  return end;
}

/** The parts still in force; a past time counts as off (the server leaves them out too). */
export function current(status: AccountStatus | undefined, now: Date) {
  const dndUntil = status?.dndUntil && new Date(status.dndUntil) > now ? status.dndUntil : null;
  const custom = status?.customStatus;
  const customStatus =
    custom && (custom.emoji || custom.text) && (!custom.expiresAt || new Date(custom.expiresAt) > now)
      ? custom
      : null;
  return { dndUntil, customStatus };
}

/** The zone sent when the browser has none or the server does not know the browser's. */
export const FALLBACK_TIME_ZONE = 'UTC';

/** The browser's IANA time zone, sent with every change (used only for reminders and "until tomorrow"); UTC when it has none. */
export function browserTimeZone(): string {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  return typeof zone === 'string' && zone.length > 0 ? zone : FALLBACK_TIME_ZONE;
}

/** A whole status from what is in force now and one change. */
export function nextStatus(
  status: AccountStatus | undefined,
  now: Date,
  change: Partial<Pick<StatusInput, 'dndUntil' | 'customStatus'>>,
): StatusInput {
  const kept = current(status, now);
  return {
    dndUntil: change.dndUntil === undefined ? kept.dndUntil : change.dndUntil,
    customStatus: change.customStatus === undefined ? kept.customStatus : change.customStatus,
    timeZone: browserTimeZone(),
  };
}

/** The value of an `<input type="datetime-local">` for a date, in local time. */
export function toLocalInput(value: Date): string {
  const pad = (part: number) => String(part).padStart(2, '0');
  return `${String(value.getFullYear())}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}`;
}

/** Reads an `<input type="datetime-local">` value as local time; null when empty or malformed. */
export function fromLocalInput(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const [, year, month, day, hour, minute] = match.map(Number) as [
    number,
    number,
    number,
    number,
    number,
    number,
  ];
  return new Date(year, month - 1, day, hour, minute);
}
