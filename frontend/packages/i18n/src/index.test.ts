import { describe, expect, it } from 'vitest';
import {
  formatDateTime,
  formatDuration,
  formatMessageDay,
  formatMessageTime,
  resolveLocale,
  translate,
  type Messages,
} from './index';

describe('resolveLocale', () => {
  it('ResolveLocale_WithSeveralPreferences_PicksTheFirstSupported', () => {
    expect(resolveLocale(['de-DE', 'tr-TR', 'en-US'])).toBe('tr');
  });

  it('ResolveLocale_WithoutASupportedLanguage_FallsBackToEnglish', () => {
    expect(resolveLocale(['de-DE', 'fr'])).toBe('en');
    expect(resolveLocale([])).toBe('en');
  });

  it('ResolveLocale_WithTurkishCasing_StillMatches', () => {
    expect(resolveLocale(['TR'])).toBe('tr');
  });
});

describe('translate', () => {
  const messages: Messages<'greet'> = { en: { greet: 'Hello {name}' }, tr: { greet: 'Merhaba {name}' } };

  it('Translate_WithPlaceholders_FillsKnownAndKeepsUnknown', () => {
    expect(translate(messages, 'tr', 'greet', { name: 'Ayşe' })).toBe('Merhaba Ayşe');
    expect(translate(messages, 'en', 'greet')).toBe('Hello {name}');
  });
});

describe('formatDuration', () => {
  it('FormatDuration_WithSeconds_RoundsUpToMinutesOrHours', () => {
    expect(formatDuration(30, 'en')).toBe('1 minute');
    expect(formatDuration(7200, 'en')).toBe('2 hours');
    expect(formatDuration(3601, 'tr')).toBe('2 saat');
  });
});

describe('formatDateTime', () => {
  it('FormatDateTime_InEachLanguage_UsesThatLanguagesFormat', () => {
    const value = new Date('2026-11-22T14:30:00Z');

    expect(formatDateTime(value, 'en', 'UTC')).toBe('November 22, 2026 at 2:30 PM');
    expect(formatDateTime(value, 'tr', 'UTC')).toBe('22 Kasım 2026 14:30');
  });
});

describe('formatMessageTime', () => {
  const now = new Date('2026-11-22T20:00:00Z');

  it('FormatMessageTime_SameDay_ShowsOnlyTheTime', () => {
    const value = new Date('2026-11-22T14:30:00Z');

    expect(formatMessageTime(value, 'en', now, 'UTC')).toBe('2:30 PM');
    expect(formatMessageTime(value, 'tr', now, 'UTC')).toBe('14:30');
  });

  it('FormatMessageTime_EarlierDay_ShowsTheDateToo', () => {
    const value = new Date('2026-11-21T14:30:00Z');

    expect(formatMessageTime(value, 'en', now, 'UTC')).toBe('Nov 21, 2:30 PM');
    expect(formatMessageTime(value, 'tr', now, 'UTC')).toBe('21 Kas 14:30');
  });
});

describe('formatMessageDay', () => {
  const now = new Date('2026-11-22T20:00:00Z');

  it('FormatMessageDay_TodayAndYesterday_AreRelative', () => {
    expect(formatMessageDay(new Date('2026-11-22T01:00:00Z'), 'en', now, 'UTC').relative).toBe('today');
    expect(formatMessageDay(new Date('2026-11-21T23:00:00Z'), 'tr', now, 'UTC').relative).toBe('yesterday');
  });

  it('FormatMessageDay_EarlierDay_ShowsTheFullDate', () => {
    const day = formatMessageDay(new Date('2026-11-20T14:30:00Z'), 'en', now, 'UTC');

    expect(day.relative).toBeUndefined();
    expect(day.text).toBe('Friday, November 20, 2026');
    expect(formatMessageDay(new Date('2026-11-20T14:30:00Z'), 'tr', now, 'UTC').text).toBe(
      '20 Kasım 2026 Cuma',
    );
  });

  it('FormatMessageDay_SameDay_HasTheSameKey', () => {
    const morning = formatMessageDay(new Date('2026-11-20T08:00:00Z'), 'en', now, 'UTC');
    const evening = formatMessageDay(new Date('2026-11-20T22:00:00Z'), 'en', now, 'UTC');

    expect(morning.key).toBe(evening.key);
  });
});
