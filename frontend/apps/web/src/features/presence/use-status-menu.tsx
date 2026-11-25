import { useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert, type MenuEntry } from '@backroom/ui';
import { formatDuration, formatMessageTime } from '@backroom/i18n';
import { EmojiText } from '@/features/emoji';
import { useLocale, useTranslate } from '@/shared/locale';
import { usePresenceEnabled } from './api';
import { messages } from './messages';
import { MY_STATUS, useMyStatus, useSetStatus } from './queries';
import { current, nextStatus } from './status';
import { PresenceDot, type OwnPresence } from './presence-dot';
import { CustomStatusDialog, DndDialog } from './status-dialogs';
import { statusErrorText } from './status-error';
import { useLocalActivity } from './use-activity';
import { useExpiryClock } from './use-expiry-clock';

export type StatusMenu = {
  /** The status part of the menu, before "Odama dön". */
  entries: MenuEntry[];
  /** The custom status as one line ("🎯 Odaklanıyorum · 1 saat"), if one is in force. */
  line: string | undefined;
  presence: OwnPresence;
  /** The presence in words, for the dot's label. */
  presenceLabel: string;
  /** The dialogs the menu opens and the errors of changes made from the menu itself. */
  overlay: ReactNode;
};

type Open = 'dnd' | 'custom' | undefined;

/**
 * The active status menu (docs/design: Thread, the `.pop` block): the custom status and a way to clear it,
 * "Çevrimiçi" (turns do not disturb and away off), "Boşta" (chosen by hand it stays until another choice; it is also
 * shown while the person is idle), "Rahatsız etme" and "Özel durum ayarla…". Do not disturb and a custom status end
 * by themselves: the menu is drawn again at that moment and the status is asked for again. Undefined while presence
 * is off: the account menu then keeps its passive entries.
 */
export function useStatusMenu(): StatusMenu | undefined {
  const enabled = usePresenceEnabled();
  const t = useTranslate(messages);
  const locale = useLocale();
  const status = useMyStatus();
  const setStatus = useSetStatus();
  const activity = useLocalActivity();
  const [open, setOpen] = useState<Open>(undefined);
  const queryClient = useQueryClient();
  const now = useExpiryClock('me', [status.data?.dndUntil, status.data?.customStatus?.expiresAt], () => {
    void queryClient.invalidateQueries({ queryKey: MY_STATUS });
  });
  if (!enabled) return undefined;

  // Shown by precedence: do not disturb, away chosen by hand, automatic idle, online.
  const { dndUntil, away, customStatus } = current(status.data, now);
  const presence: OwnPresence = dndUntil ? 'dnd' : away || activity === 'idle' ? 'away' : 'online';
  const presenceLabel =
    presence === 'dnd' ? t('doNotDisturb') : presence === 'away' ? t('away') : t('online');

  let line: string | undefined;
  if (customStatus) {
    const words = [customStatus.emoji, customStatus.text].filter(Boolean).join(' ');
    line = customStatus.expiresAt
      ? t('statusUntil', {
          status: words,
          duration: formatDuration(
            (new Date(customStatus.expiresAt).getTime() - now.getTime()) / 1000,
            locale,
          ),
        })
      : words;
  }

  const change = (part: Parameters<typeof nextStatus>[2]) => {
    if (setStatus.isPending) return;
    setStatus.mutate(nextStatus(status.data, new Date(), part));
  };

  const entries: MenuEntry[] = [
    ...(line
      ? [
          {
            kind: 'item' as const,
            id: 'current-status',
            label: <EmojiText text={line} />,
            onSelect: () => {
              setOpen('custom');
            },
          },
          {
            kind: 'item' as const,
            id: 'clear-status',
            label: t('clearStatus'),
            onSelect: () => {
              change({ customStatus: null });
            },
          },
          { kind: 'separator' as const, id: 'sep-status' },
        ]
      : []),
    {
      kind: 'item',
      id: 'online',
      label: t('online'),
      icon: <PresenceDot presence="online" />,
      checked: presence === 'online',
      onSelect: () => {
        if (dndUntil || away) change({ dndUntil: null, away: false });
      },
    },
    {
      kind: 'item',
      id: 'away',
      label: t('away'),
      detail: t('awayHint'),
      icon: <PresenceDot presence="away" />,
      checked: presence === 'away',
      onSelect: () => {
        if (!away) change({ away: true });
      },
    },
    {
      kind: 'item',
      id: 'dnd',
      label: t('doNotDisturb'),
      detail: dndUntil
        ? t('dndEnds', { time: formatMessageTime(new Date(dndUntil), locale, now) })
        : t('dndHint'),
      icon: <PresenceDot presence="dnd" />,
      checked: presence === 'dnd',
      onSelect: () => {
        setOpen('dnd');
      },
    },
    { kind: 'separator', id: 'sep-custom' },
    {
      kind: 'item',
      id: 'custom',
      label: t('customStatus'),
      onSelect: () => {
        setOpen('custom');
      },
    },
  ];

  const close = () => {
    setOpen(undefined);
  };
  const overlay = (
    <>
      {setStatus.error && (
        <div className="bp-notice">
          <Alert>{statusErrorText(setStatus.error, t)}</Alert>
        </div>
      )}
      {open === 'dnd' && <DndDialog status={status.data} onClose={close} />}
      {open === 'custom' && <CustomStatusDialog status={status.data} onClose={close} />}
    </>
  );

  return { entries, line, presence, presenceLabel, overlay };
}
