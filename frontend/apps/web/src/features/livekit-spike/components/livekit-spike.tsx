import { useEffect, useMemo, useRef, useState } from 'react';
import { Room, RoomEvent, Track, type Participant } from 'livekit-client';
import { detectCapabilities } from '../lib/capabilities';
import { TrackView } from './track-view';

// Phase 0 Tauri spike (docs/spikes/tauri.md): a throwaway, developer-only screen behind a flag.
// Its texts are English and inline on purpose; it is deleted once the spike gate is decided.

interface ShownTrack {
  key: string;
  track: Track;
  label: string;
}

type MediaKind = 'camera' | 'microphone' | 'screen';

interface MediaState {
  camera: boolean;
  microphone: boolean;
  screen: boolean;
}

const DEFAULT_URL = 'ws://localhost:7880';
const ALL_OFF: MediaState = { camera: false, microphone: false, screen: false };

function collectTracks(room: Room): ShownTrack[] {
  const participants: Participant[] = [room.localParticipant, ...room.remoteParticipants.values()];
  return participants.flatMap((participant) =>
    [...participant.trackPublications.values()].flatMap((publication) => {
      const track = publication.track;
      // Never play back our own microphone: it would echo on the same machine.
      if (!track || (participant.isLocal && track.kind === Track.Kind.Audio)) {
        return [];
      }
      return [
        {
          key: `${participant.identity}:${publication.trackSid}`,
          track,
          label: `${participant.identity} · ${publication.source}${participant.isLocal ? ' (local)' : ''}`,
        },
      ];
    }),
  );
}

function readMediaState(room: Room): MediaState {
  const local = room.localParticipant;
  return {
    camera: local.isCameraEnabled,
    microphone: local.isMicrophoneEnabled,
    screen: local.isScreenShareEnabled,
  };
}

export function LivekitSpike() {
  const capabilities = useMemo(() => detectCapabilities(), []);
  const [url, setUrl] = useState(DEFAULT_URL);
  const [token, setToken] = useState('');
  const [room, setRoom] = useState<Room | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [tracks, setTracks] = useState<ShownTrack[]>([]);
  const [media, setMedia] = useState<MediaState>(ALL_OFF);
  const [audioBlocked, setAudioBlocked] = useState(false);
  const [log, setLog] = useState<string[]>([]);
  const roomRef = useRef<Room | null>(null);

  const write = (line: string) => {
    setLog((previous) => [`${new Date().toISOString().slice(11, 19)} ${line}`, ...previous].slice(0, 80));
  };

  useEffect(
    () => () => {
      void roomRef.current?.disconnect();
      roomRef.current = null;
    },
    [],
  );

  const connect = async () => {
    if (connecting || roomRef.current) {
      return;
    }

    const next = new Room({ adaptiveStream: true, dynacast: true });
    roomRef.current = next;
    setConnecting(true);

    // Events from a room that is no longer the active one are ignored.
    const isActive = () => roomRef.current === next;
    const refresh = () => {
      if (isActive()) {
        setTracks(collectTracks(next));
        setMedia(readMediaState(next));
      }
    };

    next
      .on(RoomEvent.TrackSubscribed, refresh)
      .on(RoomEvent.TrackUnsubscribed, refresh)
      .on(RoomEvent.LocalTrackPublished, refresh)
      .on(RoomEvent.LocalTrackUnpublished, refresh)
      .on(RoomEvent.TrackMuted, refresh)
      .on(RoomEvent.TrackUnmuted, refresh)
      .on(RoomEvent.ParticipantConnected, (participant) => {
        write(`joined: ${participant.identity}`);
      })
      .on(RoomEvent.ParticipantDisconnected, (participant) => {
        write(`left: ${participant.identity}`);
        refresh();
      })
      .on(RoomEvent.MediaDevicesError, (error) => {
        write(`media device error: ${String(error)}`);
      })
      .on(RoomEvent.AudioPlaybackStatusChanged, () => {
        if (isActive()) {
          setAudioBlocked(!next.canPlaybackAudio);
          write(`audio playback ${next.canPlaybackAudio ? 'allowed' : 'blocked by autoplay policy'}`);
        }
      })
      .on(RoomEvent.Reconnecting, () => {
        write('reconnecting');
      })
      .on(RoomEvent.Reconnected, () => {
        write('reconnected');
      })
      .on(RoomEvent.Disconnected, (reason) => {
        write(`disconnected: ${String(reason)}`);
        if (isActive()) {
          roomRef.current = null;
          setRoom(null);
          setTracks([]);
          setMedia(ALL_OFF);
          setAudioBlocked(false);
        }
      });

    try {
      const started = performance.now();
      await next.connect(url, token);
      if (!isActive()) {
        await next.disconnect();
        return;
      }
      write(`connected to ${next.name} in ${String(Math.round(performance.now() - started))} ms`);
      setRoom(next);
      setAudioBlocked(!next.canPlaybackAudio);
      refresh();
    } catch (error) {
      write(`connect failed: ${String(error)}`);
      if (isActive()) {
        roomRef.current = null;
      }
    } finally {
      setConnecting(false);
    }
  };

  const toggle = async (kind: MediaKind) => {
    if (!room) {
      return;
    }
    const on = !media[kind];
    try {
      if (kind === 'camera') {
        await room.localParticipant.setCameraEnabled(on);
      } else if (kind === 'microphone') {
        await room.localParticipant.setMicrophoneEnabled(on);
      } else {
        await room.localParticipant.setScreenShareEnabled(on, { audio: false });
      }
      write(`${kind} ${on ? 'on' : 'off'}`);
    } catch (error) {
      write(`${kind} failed: ${String(error)}`);
    } finally {
      setMedia(readMediaState(room));
      setTracks(collectTracks(room));
    }
  };

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', padding: 16, display: 'grid', gap: 16 }}>
      <h1>LiveKit spike</h1>

      <section aria-labelledby="capabilities-heading">
        <h2 id="capabilities-heading">Capabilities</h2>
        <pre>{JSON.stringify(capabilities, null, 2)}</pre>
      </section>

      {room ? (
        <section aria-labelledby="controls-heading">
          <h2 id="controls-heading">Room {room.name}</h2>
          {(['microphone', 'camera', 'screen'] as const).map((kind) => (
            <button key={kind} type="button" aria-pressed={media[kind]} onClick={() => void toggle(kind)}>
              {media[kind] ? `Stop ${kind}` : `Start ${kind}`}
            </button>
          ))}
          {audioBlocked && (
            <button type="button" onClick={() => void room.startAudio()}>
              Allow audio playback
            </button>
          )}
          <button type="button" onClick={() => void room.disconnect()}>
            Leave
          </button>
        </section>
      ) : (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void connect();
          }}
          style={{ display: 'grid', gap: 8, maxWidth: 640 }}
        >
          <label>
            Server URL{' '}
            <input
              value={url}
              onChange={(event) => {
                setUrl(event.target.value);
              }}
              required
              style={{ width: '100%' }}
            />
          </label>
          <label>
            Access token{' '}
            <textarea
              value={token}
              onChange={(event) => {
                setToken(event.target.value);
              }}
              required
              rows={4}
              style={{ width: '100%' }}
            />
          </label>
          <button type="submit" disabled={connecting}>
            {connecting ? 'Connecting…' : 'Connect'}
          </button>
        </form>
      )}

      <section
        aria-labelledby="tracks-heading"
        style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))', gap: 8 }}
      >
        <h2 id="tracks-heading" style={{ gridColumn: '1 / -1' }}>
          Tracks ({tracks.length})
        </h2>
        {tracks.map((shown) => (
          <TrackView key={shown.key} track={shown.track} label={shown.label} />
        ))}
      </section>

      <section aria-labelledby="log-heading">
        <h2 id="log-heading">Log</h2>
        <pre>{log.join('\n')}</pre>
      </section>
    </main>
  );
}
