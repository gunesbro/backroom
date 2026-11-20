import { describe, expect, it } from 'vitest';
import { formatDateTime, formatDuration, resolveLocale, translate, type Messages } from './index';

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
