import { translate, type Locale, type Messages } from '@backroom/i18n';

/** The BCP 47 tag each language formats with; explicit, so formatting never depends on the machine's locale. */
export const FORMAT_TAGS: Readonly<Record<Locale, string>> = { en: 'en-US', tr: 'tr-TR' };

/** A translator for one screen's messages in a language. */
export function translator<TKey extends string>(messages: Messages<TKey>, locale: Locale) {
  return (key: TKey, values?: Readonly<Record<string, string | number>>): string =>
    translate(messages, locale, key, values);
}

/** The relative time of a heartbeat ("2 minutes ago"), in the person's language. */
export function formatRelative(then: Date, now: Date, locale: Locale): string {
  const seconds = Math.round((then.getTime() - now.getTime()) / 1000);
  const format = new Intl.RelativeTimeFormat(FORMAT_TAGS[locale], { numeric: 'auto' });
  const abs = Math.abs(seconds);
  if (abs < 60) return format.format(seconds, 'second');
  if (abs < 3600) return format.format(Math.round(seconds / 60), 'minute');
  if (abs < 86_400) return format.format(Math.round(seconds / 3600), 'hour');
  return format.format(Math.round(seconds / 86_400), 'day');
}
