import { WebSocket } from 'k6/websockets';

// A minimal SignalR client for the MessagePack protocol (the hub accepts nothing else, and only WebSockets). It
// encodes the few invocations a client sends (handshake, SubscribeChannel, ping) and does not decode the live events:
// the sender writes its send time into the message text ("lt:<epoch ms>:"), and the client finds that stamp in the
// frame bytes. This measures delivery through the whole path (live event, Valkey, SignalR) without a decoder.
const RECORD_SEPARATOR = '\u001e';
const STAMP = [0x6c, 0x74, 0x3a]; // "lt:"

function frame(payload) {
  const length = [];
  let n = payload.length;
  do {
    let b = n & 0x7f;
    n >>>= 7;
    if (n > 0) b |= 0x80;
    length.push(b);
  } while (n > 0);
  return new Uint8Array([...length, ...payload]).buffer;
}

const PING = frame([0x91, 0x06]); // [6] = Ping

function encode(value, out) {
  if (value === null) {
    out.push(0xc0);
  } else if (typeof value === 'number') {
    if (!Number.isInteger(value) || value < 0 || value > 127) throw new Error('only small integers are needed');
    out.push(value);
  } else if (typeof value === 'string') {
    const bytes = Array.from(value, (c) => c.charCodeAt(0)); // ASCII only (ids and method names)
    if (bytes.length < 32) out.push(0xa0 | bytes.length);
    else if (bytes.length < 256) out.push(0xd9, bytes.length);
    else throw new Error('string too long');
    out.push(...bytes);
  } else if (Array.isArray(value)) {
    if (value.length > 15) throw new Error('array too long');
    out.push(0x90 | value.length);
    value.forEach((item) => encode(item, out));
  } else {
    out.push(0x80); // empty map (message headers)
  }
}

/** Invocation without an invocation id: [1, {}, null, target, args]. */
function invocation(target, args) {
  const out = [];
  encode([1, {}, null, target, args], out);
  return frame(out);
}

/** The send time in the first "lt:<ms>:" stamp of a binary frame, or null. */
export function stampOf(buffer) {
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i + 4 < bytes.length; i++) {
    if (bytes[i] === STAMP[0] && bytes[i + 1] === STAMP[1] && bytes[i + 2] === STAMP[2]) {
      let ms = 0;
      let j = i + 3;
      while (j < bytes.length && bytes[j] >= 0x30 && bytes[j] <= 0x39) {
        ms = ms * 10 + (bytes[j] - 0x30);
        j++;
      }
      if (j > i + 3 && bytes[j] === 0x3a) return ms;
    }
  }
  return null;
}

/**
 * Opens the hub, subscribes to the channels and calls onVisible(milliseconds since the stamp) for each message posted.
 * onReady(milliseconds from connecting until subscribed) is called once. The connection closes after holdMs.
 * The k6 iteration ends when the connection has closed (the event loop is empty).
 */
export function listen({ cellUrl, token, orgId, channelIds, holdMs, onReady, onVisible, onError }) {
  const url = `${cellUrl.replace(/^http/, 'ws').replace(/\/$/, '')}/hubs/realtime?access_token=${encodeURIComponent(token)}`;
  const started = Date.now();
  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  let pinger;

  ws.onopen = () => ws.send(`{"protocol":"messagepack","version":1}${RECORD_SEPARATOR}`);
  ws.onmessage = (event) => {
    if (typeof event.data === 'string') {
      // The handshake answer ("{}" or an error); subscribe once it is accepted.
      if (event.data.startsWith('{"error"')) {
        onError('handshake');
        ws.close();
        return;
      }
      channelIds.forEach((id) => ws.send(invocation('SubscribeChannel', [orgId, id])));
      onReady(Date.now() - started);
      pinger = setInterval(() => ws.send(PING), 10_000);
      setTimeout(() => ws.close(), holdMs);
      return;
    }
    const sentAt = stampOf(event.data);
    if (sentAt !== null) {
      const elapsed = Date.now() - sentAt;
      if (elapsed >= 0 && elapsed < 60_000) onVisible(elapsed);
    }
  };
  ws.onerror = () => onError('socket');
  ws.onclose = () => {
    if (pinger) clearInterval(pinger);
  };
}
