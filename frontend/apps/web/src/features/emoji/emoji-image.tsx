import { memo, useState, type ReactNode } from 'react';
import { useLocale } from '@/shared/locale';
import { emojiImageUrl } from './images';
import { loadEmojiData } from './load';
import { splitEmoji } from './segment';
import './emoji-image.css';

// Emoji whose image could not be loaded (an emoji newer than the image set, or no connection): they stay text.
// Bounded: past the limit an emoji simply tries its image again the next time it is drawn.
const MAX_MISSING = 500;
const missing = new Set<string>();

/**
 * One emoji as an image of the app's own image set, so it looks the same on every system. To the page it still is
 * its character: `alt` carries it, which is what is read aloud, found and copied. The name shows as a tooltip, from
 * `label` or, without one, from the emoji data once the pointer is on the emoji. An emoji without an image is shown
 * as text.
 */
export const EmojiImage = memo(function EmojiImage({
  emoji,
  label,
  large = false,
}: {
  emoji: string;
  /** The emoji's name, where the caller knows it. */
  label?: string | undefined;
  /** For messages that are only emoji. */
  large?: boolean;
}) {
  const locale = useLocale();
  const [failed, setFailed] = useState<string>();
  const [name, setName] = useState<{ emoji: string; label: string }>();
  if (failed === emoji || missing.has(emoji)) return emoji;
  const title = label ?? (name?.emoji === emoji ? name.label : undefined);
  return (
    <img
      className={large ? 'be-emoji be-emoji--large' : 'be-emoji'}
      src={emojiImageUrl(emoji)}
      alt={emoji}
      title={title}
      data-emoji=""
      draggable={false}
      loading="lazy"
      decoding="async"
      onError={() => {
        if (missing.size < MAX_MISSING) missing.add(emoji);
        setFailed(emoji);
      }}
      onPointerEnter={
        title === undefined
          ? () => {
              loadEmojiData(locale).then(
                (data) => {
                  const known = data.byEmoji.get(emoji)?.label;
                  if (known) setName({ emoji, label: known });
                },
                () => {
                  // No name: the emoji itself is still there.
                },
              );
            }
          : undefined
      }
    />
  );
});

/** A text with its emoji as images; everything else stays text. */
export function EmojiText({ text, large = false }: { text: string; large?: boolean }): ReactNode {
  const parts = splitEmoji(text);
  if (parts.every((part) => typeof part === 'string')) return text;
  return parts.map((part, index) =>
    typeof part === 'string' ? part : <EmojiImage key={index} emoji={part.emoji} large={large} />,
  );
}
