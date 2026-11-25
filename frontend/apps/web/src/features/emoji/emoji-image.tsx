import { memo, useState, type ReactNode } from 'react';
import { useLocale } from '@/shared/locale';
import { emojiImageUrl } from './images';
import { loadEmojiData } from './load';
import { addMissingEmojiImage, isMissingEmojiImage } from './missing-images';
import { splitEmoji } from './segment';
import './emoji-image.css';

/**
 * One emoji as an image of the app's own image set, so it looks the same on every system. To the page it still is
 * its character: `alt` carries it, which is what is read aloud, found and copied. The name shows as a tooltip, from
 * `label` or, without one, from the emoji data once the pointer is on the emoji. An emoji without an image is shown
 * as text.
 */
export const EmojiImage = memo(function EmojiImage({
  emoji,
  label,
  tooltip = true,
  large = false,
  lazy = false,
}: {
  emoji: string;
  /** The emoji's name, where the caller knows it. */
  label?: string | undefined;
  /** Whether the name shows as a tooltip; off where what the emoji sits in already has one. */
  tooltip?: boolean;
  /** For messages that are only emoji. */
  large?: boolean;
  /**
   * Whether the image waits until it is near the screen: for long lists that are all in the page (the picker).
   * Lists that only hold what is on screen (messages) load at once, so an emoji is there when its row is.
   */
  lazy?: boolean;
}) {
  const locale = useLocale();
  // Only makes the emoji draw again after its image failed; whether it is shown as text is in missing-images.ts.
  const [, setFailures] = useState(0);
  const [name, setName] = useState<{ emoji: string; label: string }>();
  if (isMissingEmojiImage(emoji)) return emoji;
  const title = tooltip ? (label ?? (name?.emoji === emoji ? name.label : undefined)) : undefined;
  return (
    <img
      className={large ? 'be-emoji be-emoji--large' : 'be-emoji'}
      src={emojiImageUrl(emoji)}
      alt={emoji}
      title={title}
      data-emoji=""
      draggable={false}
      loading={lazy ? 'lazy' : undefined}
      decoding="async"
      onError={() => {
        addMissingEmojiImage(emoji);
        setFailures((count) => count + 1);
      }}
      onPointerEnter={
        tooltip && title === undefined
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
