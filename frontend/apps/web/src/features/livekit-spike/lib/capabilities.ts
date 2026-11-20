export interface MediaCapabilities {
  userAgent: string;
  secureContext: boolean;
  rtcPeerConnection: boolean;
  getUserMedia: boolean;
  getDisplayMedia: boolean;
  /** Codecs the web view can send; VP8/H264 availability is the likely failure point on WebKitGTK. */
  videoCodecs: string[];
  audioCodecs: string[];
}

function senderCodecs(kind: 'audio' | 'video'): string[] {
  if (typeof RTCRtpSender === 'undefined' || typeof RTCRtpSender.getCapabilities !== 'function') {
    return [];
  }
  const codecs = RTCRtpSender.getCapabilities(kind)?.codecs ?? [];
  return [...new Set(codecs.map((codec) => codec.mimeType))];
}

/** What this web view supports, reported on screen so device tests can record it. */
export function detectCapabilities(): MediaCapabilities {
  const mediaDevices = 'mediaDevices' in navigator ? navigator.mediaDevices : undefined;

  return {
    userAgent: navigator.userAgent,
    secureContext: window.isSecureContext,
    rtcPeerConnection: typeof RTCPeerConnection !== 'undefined',
    getUserMedia: typeof mediaDevices?.getUserMedia === 'function',
    getDisplayMedia: typeof mediaDevices?.getDisplayMedia === 'function',
    videoCodecs: senderCodecs('video'),
    audioCodecs: senderCodecs('audio'),
  };
}
