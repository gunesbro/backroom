import { lazy } from 'react';

/** The spike screen is available in development and in builds made with VITE_ENABLE_SPIKES=true. */
export const isLivekitSpikeEnabled = import.meta.env.DEV || import.meta.env.VITE_ENABLE_SPIKES === 'true';

export const LIVEKIT_SPIKE_HASH = '#/spike/livekit';

/** Loaded on demand so livekit-client stays out of the main bundle. */
export const LivekitSpike = lazy(async () => {
  const module = await import('./components/livekit-spike');
  return { default: module.LivekitSpike };
});
