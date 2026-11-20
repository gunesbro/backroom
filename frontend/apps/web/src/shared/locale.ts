import { createContext, useCallback, useContext } from 'react';
import { translate, type Locale, type Messages } from '@backroom/i18n';

export const LocaleContext = createContext<Locale>('en');

export function useLocale(): Locale {
  return useContext(LocaleContext);
}

/** A translator for one feature's messages in the current language. */
export function useTranslate<TKey extends string>(messages: Messages<TKey>) {
  const locale = useLocale();
  return useCallback(
    (key: TKey, values?: Readonly<Record<string, string | number>>) =>
      translate(messages, locale, key, values),
    [messages, locale],
  );
}
