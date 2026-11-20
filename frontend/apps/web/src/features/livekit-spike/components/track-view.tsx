import { useEffect, useRef } from 'react';
import { Track } from 'livekit-client';

interface TrackViewProps {
  track: Track;
  label: string;
}

/** Attaches a LiveKit track to a media element for as long as the component is mounted. */
export function TrackView({ track, label }: TrackViewProps) {
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const element = track.attach();
    element.setAttribute('aria-label', label);
    if (track.kind === Track.Kind.Video) {
      element.style.width = '100%';
      element.style.background = '#000';
    }
    container.current?.appendChild(element);

    return () => {
      track.detach(element);
      element.remove();
    };
  }, [track, label]);

  return (
    <figure style={{ margin: 0 }}>
      <div ref={container} />
      <figcaption>{label}</figcaption>
    </figure>
  );
}
