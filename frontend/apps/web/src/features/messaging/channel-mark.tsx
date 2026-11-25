import { Icon } from '@backroom/ui';

/**
 * A lock for a private channel, a hash otherwise. The lock is named for screen readers with `label`; without one it
 * is decoration and the text next to it says that the channel is private.
 */
export function ChannelMark({
  isPrivate,
  label,
  size = 14,
}: {
  isPrivate: boolean;
  label?: string;
  size?: number;
}) {
  return isPrivate ? (
    <span className="bm-mark">
      <Icon name="lock" size={size} />
      {label !== undefined && <span className="bm-sr-only">{label}</span>}
    </span>
  ) : (
    <span aria-hidden="true">#</span>
  );
}
