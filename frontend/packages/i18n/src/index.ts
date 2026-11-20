// Turkish and English translations and locale-aware formatting.
// Public surface of @backroom/i18n.

/** The languages the product ships. English is the fallback when nothing else matches. */
export type Locale = 'en' | 'tr';

export const DEFAULT_LOCALE: Locale = 'en';

/** A feature's texts: the same keys in every language. */
export type Messages<TKey extends string> = Record<Locale, Record<TKey, string>>;

/** The first supported language among the person's preferences (BCP 47 tags, most preferred first). */
export function resolveLocale(preferences: readonly string[]): Locale {
  for (const tag of preferences) {
    const language = tag.toLowerCase().split('-')[0];
    if (language === 'tr' || language === 'en') {
      return language;
    }
  }

  return DEFAULT_LOCALE;
}

/**
 * The text for `key` in `locale`, with `{name}` placeholders filled from `values`. Values are inserted as
 * plain text; rendering them is React's job, which escapes them.
 */
export function translate<TKey extends string>(
  messages: Messages<TKey>,
  locale: Locale,
  key: TKey,
  values: Readonly<Record<string, string | number>> = {},
): string {
  const template = messages[locale][key];
  return template.replace(/\{(\w+)\}/g, (placeholder, name: string) =>
    name in values ? String(values[name]) : placeholder,
  );
}

/** A waiting time such as "3 hours" or "3 saat", rounded up to whole minutes or hours. */
export function formatDuration(seconds: number, locale: Locale): string {
  const format = (value: number, unit: 'hour' | 'minute') =>
    new Intl.NumberFormat(locale, { style: 'unit', unit, unitDisplay: 'long' }).format(value);
  const minutes = Math.max(1, Math.ceil(seconds / 60));
  return minutes >= 60 ? format(Math.ceil(minutes / 60), 'hour') : format(minutes, 'minute');
}

/** The BCP 47 tag each language formats with; explicit, so formatting never depends on the machine's locale. */
const FORMAT_TAGS: Readonly<Record<Locale, string>> = { en: 'en-US', tr: 'tr-TR' };

/** A date and time such as "November 22, 2026 at 3:30 PM" or "22 Kasım 2026 15:30", in the person's time zone. */
export function formatDateTime(value: Date, locale: Locale, timeZone?: string): string {
  return new Intl.DateTimeFormat(FORMAT_TAGS[locale], {
    dateStyle: 'long',
    timeStyle: 'short',
    ...(timeZone === undefined ? {} : { timeZone }),
  }).format(value);
}
