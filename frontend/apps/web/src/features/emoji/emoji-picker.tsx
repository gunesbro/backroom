import { useEffect, useState, type ComponentType } from 'react';
import type { Locale, Messages } from '@backroom/i18n';
import { Alert, Status } from '@backroom/ui';
import { useLocale, useTranslate } from '@/shared/locale';
import type { EmojiData } from './data';
import { loadEmojiData } from './load';

// Only these two texts load with the app; the picker's own texts come with its chunk.
const messages: Messages<'loading' | 'loadFailed'> = {
  en: { loading: 'Loading emoji…', loadFailed: "Emoji couldn't be loaded. Close this and try again." },
  tr: { loading: 'Emojiler yükleniyor…', loadFailed: 'Emojiler yüklenemedi. Kapatıp tekrar dene.' },
};

export interface EmojiPickerProps {
  /** Called with the emoji that was picked (one emoji, with its skin tone). Closing the picker is the caller's job. */
  onPick: (emoji: string) => void;
  /** Called on Escape; without it, Escape is left to whatever the picker sits in. */
  onClose?: () => void;
  /** Whether the search box takes focus when the picker appears (default: yes). */
  autoFocus?: boolean;
}

type PanelProps = Required<Omit<EmojiPickerProps, 'onClose'>> & {
  data: EmojiData;
  onClose: (() => void) | undefined;
};

type Loaded = { locale: Locale; Panel: ComponentType<PanelProps>; data: EmojiData };

/**
 * The shared emoji picker: people pick emoji from this list and never type them. The picker and its data load as
 * their own chunks when it first appears; all of it is served by the app itself. It renders in place, so the caller
 * decides where it sits (a dialog, a popover) and what happens after a pick.
 */
export function EmojiPicker({ onPick, onClose, autoFocus = true }: EmojiPickerProps) {
  const locale = useLocale();
  const t = useTranslate(messages);
  const [loaded, setLoaded] = useState<Loaded | 'failed'>();

  useEffect(() => {
    let live = true;
    Promise.all([import('./picker-panel'), loadEmojiData(locale)]).then(
      ([panel, data]) => {
        if (live) setLoaded({ locale, Panel: panel.PickerPanel, data });
      },
      () => {
        if (live) setLoaded('failed');
      },
    );
    return () => {
      live = false;
    };
  }, [locale]);

  if (loaded === 'failed') return <Alert>{t('loadFailed')}</Alert>;
  if (loaded?.locale !== locale) return <Status>{t('loading')}</Status>;
  const { Panel, data } = loaded;
  return <Panel data={data} onPick={onPick} onClose={onClose} autoFocus={autoFocus} />;
}
