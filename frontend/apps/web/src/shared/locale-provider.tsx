import { useEffect, useState, type ReactNode } from 'react';
import { resolveLocale, type Locale } from '@backroom/i18n';
import { LocaleContext } from './locale';

/** The person's language for the whole app; it changes rarely, so a context is fine. */
export function LocaleProvider({ locale, children }: { locale?: Locale | undefined; children: ReactNode }) {
  const [browserLocale] = useState(() => resolveLocale(navigator.languages));
  const value = locale ?? browserLocale;

  useEffect(() => {
    // Screen readers and hyphenation follow the page language.
    document.documentElement.lang = value;
  }, [value]);

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>;
}
