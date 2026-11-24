import './presence.css';

export type OwnPresence = 'online' | 'away' | 'dnd';

/** A presence dot (docs/design: Thread, `.dot` and `.sdot`); decorative unless it has a label. */
export function PresenceDot({ presence, label }: { presence: OwnPresence; label?: string | undefined }) {
  return label ? (
    <span className={`bp-dot bp-dot--${presence}`} role="img" aria-label={label} />
  ) : (
    <span className={`bp-dot bp-dot--${presence}`} aria-hidden="true" />
  );
}
