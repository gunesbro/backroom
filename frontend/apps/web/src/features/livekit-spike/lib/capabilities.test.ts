import { afterEach, describe, expect, it, vi } from 'vitest';
import { detectCapabilities } from './capabilities';

describe('detectCapabilities', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('reports missing WebRTC and media APIs', () => {
    vi.stubGlobal('RTCPeerConnection', undefined);
    vi.stubGlobal('RTCRtpSender', undefined);

    const capabilities = detectCapabilities();

    expect(capabilities.rtcPeerConnection).toBe(false);
    expect(capabilities.getUserMedia).toBe(false);
    expect(capabilities.getDisplayMedia).toBe(false);
    expect(capabilities.videoCodecs).toEqual([]);
  });

  it('reports available WebRTC, media APIs and distinct codecs', () => {
    vi.stubGlobal('RTCPeerConnection', vi.fn());
    vi.stubGlobal('RTCRtpSender', {
      getCapabilities: (kind: string) => ({
        codecs:
          kind === 'video'
            ? [{ mimeType: 'video/VP8' }, { mimeType: 'video/VP8' }, { mimeType: 'video/H264' }]
            : [{ mimeType: 'audio/opus' }],
      }),
    });
    vi.stubGlobal('navigator', {
      userAgent: 'test-agent',
      mediaDevices: { getUserMedia: vi.fn(), getDisplayMedia: vi.fn() },
    });

    const capabilities = detectCapabilities();

    expect(capabilities).toMatchObject({
      userAgent: 'test-agent',
      rtcPeerConnection: true,
      getUserMedia: true,
      getDisplayMedia: true,
      videoCodecs: ['video/VP8', 'video/H264'],
      audioCodecs: ['audio/opus'],
    });
  });
});
