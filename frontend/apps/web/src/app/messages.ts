import type { Messages } from '@backroom/i18n';

// Texts of the application shell.
type Key = 'loading';

export const messages: Messages<Key> = {
  en: {
    loading: 'Loading…',
  },
  tr: {
    loading: 'Yükleniyor…',
  },
};
