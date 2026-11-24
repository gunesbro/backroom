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

/** The control plane counts messages up to this value and reports one more when there are more. */
export const MESSAGE_COUNT_CAP = 10_000;

/** A message count in the person's language; a capped one reads "10,000+". */
export function formatCount(value: number, locale: Locale): string {
  const format = new Intl.NumberFormat(FORMAT_TAGS[locale]);
  return value > MESSAGE_COUNT_CAP ? `${format.format(MESSAGE_COUNT_CAP)}+` : format.format(value);
}
